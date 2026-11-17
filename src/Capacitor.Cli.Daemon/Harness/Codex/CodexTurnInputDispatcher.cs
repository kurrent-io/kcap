using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>Outcome of a <c>turn/start</c> dispatch — the server-assigned turn id and its status
/// (<c>inProgress</c> unless the turn came back already terminal).</summary>
internal readonly record struct CodexTurnStarted(string TurnId, string? Status);

/// <summary>
/// Serializes interactive hosted-Codex input. The app server accepts a concurrent <c>turn/start</c>
/// without error — it does NOT serialize turns for you — so the daemon must: input enqueues and one
/// dispatcher chooses <c>turn/start</c> (idle) or <c>turn/steer</c> (active).
///
/// <para>Two orthogonal fields, not one flat state, because a notification can arrive mid-request:
/// <see cref="_pending"/> (driven by the request response) and <see cref="_lifecycle"/> (driven by
/// <c>turn/completed</c>). The trap: a steer the server rejects with <c>-32600</c> ("no active turn")
/// is retried EXACTLY ONCE as a <c>turn/start</c>, in the dispatcher, never dropped.</para>
/// </summary>
internal sealed class CodexTurnInputDispatcher {
    // Sends turn/start with the input as the initial prompt; returns the started turn once the RESPONSE
    // lands. Throws CodexAppServerRpcException on an error response.
    readonly Func<string, CancellationToken, Task<CodexTurnStarted>> _startTurn;
    // Sends turn/steer(expectedTurnId, input); returns when the RESPONSE lands. Throws
    // CodexAppServerRpcException(-32600) when the turn is no longer active (the retryable miss).
    readonly Func<string, string, CancellationToken, Task> _steerTurn;
    // Sends thread/compact/start; returns when the RESPONSE lands. The compaction then runs as a turn of its own.
    readonly Func<CancellationToken, Task>? _compact;
    // Notified (true/false) as a turn becomes active / goes idle — drives the runtime's turn-in-flight clock.
    readonly Action<bool>?     _onTurnInFlight;
    readonly ILogger           _logger;
    readonly CancellationToken _ct;

    readonly object            _gate = new();
    readonly Queue<InputItem>  _queue = new();
    readonly List<TaskCompletionSource> _settledWaiters = [];

    Pending   _pending   = Pending.None;
    Lifecycle _lifecycle = Lifecycle.Idle;
    string?   _activeTurnId;              // set in Active
    string?   _completedBeforeStartResp; // a turn/completed seen while _pending=Start (turn id unknown yet)
    bool      _dispatching;              // guards the fire-outside-lock dispatch against re-entrancy
    bool      _lastInFlight;             // last value handed to _onTurnInFlight, so it fires only on change
    bool      _faulted;                 // FaultAll ran; a dispatch resuming after it must not resurrect state
    bool      _sealed;                  // pre-first-turn seal: input enqueues and WAITS; nothing dispatches until Unseal
    string?   _compactionTurnId;        // the turn a pending thread/compact/start started, once turn/started names it
    bool      _compactionEndedEarly;    // that turn completed before the request's response landed
    InputItem? _compacting;             // the compaction whose turn is running

    public CodexTurnInputDispatcher(
            Func<string, CancellationToken, Task<CodexTurnStarted>> startTurn,
            Func<string, string, CancellationToken, Task> steerTurn,
            ILogger logger, CancellationToken ct, Action<bool>? onTurnInFlight = null,
            bool sealedAtStart = false, Func<CancellationToken, Task>? compact = null) {
        _startTurn      = startTurn;
        _steerTurn      = steerTurn;
        _compact        = compact;
        _logger         = logger;
        _ct             = ct;
        _onTurnInFlight = onTurnInFlight;
        _sealed         = sealedAtStart;
    }

    /// <summary>Lifts the pre-first-turn seal and pumps the queue. Called once, from the runtime's
    /// <c>BeginFirstTurnAsync</c>, after the server's source claim acks — so the held initial prompt
    /// (enqueued first, at the head) dispatches as the first <c>turn/start</c>, then any input that
    /// arrived during the sealed claim window drains FIFO behind it. Idempotent: a second call is a
    /// harmless pump.</summary>
    public void Unseal() {
        lock (_gate) _sealed = false;
        PumpDispatch();
    }

    /// <summary>True while a turn is live — including a <c>turn/start</c> whose response hasn't landed
    /// yet, so a hung start counts as in-flight (the reaper's turn-wedge ceiling must see it, matching
    /// the old "arm the clock before sending turn/start" behaviour).</summary>
    public bool TurnInFlight { get { lock (_gate) return InFlightLocked(); } }

    /// <summary>The active turn id, or null when idle — the target for a <c>turn/interrupt</c>.</summary>
    public string? CurrentTurnId { get { lock (_gate) return _lifecycle != Lifecycle.Idle ? _activeTurnId : null; } }

    /// <summary>Enqueues one input and returns a task that completes when the dispatch carrying it
    /// (a <c>turn/start</c> or an accepted <c>turn/steer</c>) succeeds — the "wait for write" contract.
    /// Faults if that dispatch errors. Throws <see cref="InputNotAdmittedException"/> synchronously
    /// once <see cref="FaultAll"/> has torn the dispatcher down, refused under the same <see cref="_gate"/>
    /// FaultAll takes so a late enqueue can never slip in and stay pending forever. An optional
    /// per-input token (the launch's linked token for the initial prompt) is linked into that input's
    /// dispatch so cancelling it aborts the send.</summary>
    public Task EnqueueAsync(string text, CancellationToken ct = default) {
        var item = new InputItem(text, ct);
        lock (_gate) {
            if (_faulted) throw new InputNotAdmittedException("Codex dispatcher is torn down; this input was not queued.");
            _queue.Enqueue(item);
        }
        PumpDispatch();
        return item.Ack.Task;
    }

    /// <summary>Queues a compaction behind any earlier input. It is sent only while no turn runs, since the app server
    /// would otherwise start it beside the active turn, and input queued after it waits until its turn completes.
    /// <c>Written</c> completes when the app server accepts the request and faults when it refuses it; <c>Finished</c>
    /// completes once the compaction is over, whichever way it ended.</summary>
    public (Task Written, Task Finished) EnqueueCompaction() {
        if (_compact is null) throw new InvalidOperationException("This dispatcher was built without a compaction sender.");

        var item = new InputItem("", CancellationToken.None, compaction: true);
        lock (_gate) {
            if (_faulted) throw new InputNotAdmittedException("Codex dispatcher is torn down; this compaction was not queued.");
            _queue.Enqueue(item);
        }
        PumpDispatch();
        return (item.Ack.Task, item.Finished!.Task);
    }

    /// <summary>Fed from the <c>turn/started</c> notification: names the turn a compaction runs as, which its request's
    /// response does not. True when this is that turn.</summary>
    public bool OnTurnStarted(string? turnId) {
        if (turnId is null) return false;
        lock (_gate) {
            if (_pending is Pending.Compact && _compactionTurnId is null) {
                _compactionTurnId = turnId;
                return true;
            }
            if (_lifecycle == Lifecycle.Compacting && _activeTurnId is null) {
                _activeTurnId = turnId;
                return true;
            }
        }
        return false;
    }

    /// <summary>Completes when the dispatcher is fully settled — no turn active, nothing pending, queue
    /// drained. The runtime's <c>WaitForTurnIdleAsync</c> composes this with its terminal signal.</summary>
    public Task WaitForSettledAsync() {
        lock (_gate) {
            if (IsSettledLocked()) return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _settledWaiters.Add(tcs);
            return tcs.Task;
        }
    }

    /// <summary>Fed from the <c>turn/completed</c> notification. Advances the lifecycle and, when a
    /// <c>turn/start</c> response has not yet identified its turn, stashes the completion so the
    /// response can reconcile "this input rode a turn that already finished".</summary>
    public void OnTurnCompleted(string? turnId) {
        InputItem? compacted = null;
        lock (_gate) {
            if (_pending is Pending.Compact) {
                if (turnId is null || _compactionTurnId is null || string.Equals(turnId, _compactionTurnId, StringComparison.Ordinal))
                    _compactionEndedEarly = true;
                return;
            }
            if (_pending is Pending.Start && _activeTurnId is null) {
                // Completion raced ahead of the start response — remember it by id; the response reconciles.
                if (turnId is not null) _completedBeforeStartResp = turnId;
                return;
            }
            // A null turn id means "the active turn completed" (the notification omitted it) — accept it
            // for whatever is active; a non-null id that doesn't match the active turn is stale.
            if (turnId is not null && _activeTurnId is not null
                && !string.Equals(_activeTurnId, turnId, StringComparison.Ordinal))
                return;
            if (_lifecycle == Lifecycle.Compacting) {
                compacted   = _compacting;
                _compacting = null;
            }
            _lifecycle    = Lifecycle.Idle;
            _activeTurnId = null;
        }
        compacted?.Finished!.TrySetResult();
        SignalTransitions();
        PumpDispatch();
    }

    /// <summary>Faults every queued and in-flight input — called on runtime teardown so no
    /// "wait for write" caller hangs.</summary>
    public void FaultAll(Exception ex) {
        List<InputItem> orphans;
        lock (_gate) {
            _faulted      = true;
            orphans       = [.. _queue];
            if (_compacting is { } running) orphans.Add(running);
            _compacting   = null;
            _queue.Clear();
            _pending      = Pending.None;
            _lifecycle    = Lifecycle.Idle;
            _activeTurnId = null;
        }
        foreach (var item in orphans) {
            item.Ack.TrySetException(ex);
            item.Finished?.TrySetResult();
        }
        SignalTransitions();
    }

    // Drains the queue as far as the invariant allows. Only one thread runs the fire-outside-lock body
    // at a time (_dispatching); other callers just mark that another pass is needed and return.
    void PumpDispatch() {
        lock (_gate) {
            if (_dispatching) return;
            _dispatching = true;
        }

        _ = DispatchLoopAsync();
    }

    async Task DispatchLoopAsync() {
      try {
        while (true) {
            InputItem item;
            bool asSteer;
            string turnId;

            lock (_gate) {
                // While sealed, input stays queued and NOTHING dispatches — no turn/* request leaves,
                // so no hook can fire before the source claim commits (the guard-2 ordering).
                if (_sealed || _pending is not Pending.None || _queue.Count == 0 || _lifecycle == Lifecycle.Compacting) {
                    _dispatching = false;
                    return;
                }
                item = _queue.Peek();
                if (item.Compaction) {
                    if (_lifecycle == Lifecycle.Active) {
                        _dispatching = false;
                        return;
                    }
                    _pending              = Pending.Compact;
                    _compactionTurnId     = null;
                    _compactionEndedEarly = false;
                    asSteer               = false;
                    turnId                = "";
                } else if (_lifecycle == Lifecycle.Active && _activeTurnId is { } t) {
                    asSteer = true;
                    turnId  = t;
                    _pending = Pending.Steer;
                } else {
                    asSteer = false;
                    turnId  = "";
                    _pending = Pending.Start;
                    _completedBeforeStartResp = null;
                }
            }

            SignalTransitions(); // a pending turn/start is already in-flight — signal before awaiting it
            if (item.Compaction) await DispatchCompactAsync(item).ConfigureAwait(false);
            else if (asSteer)    await DispatchSteerAsync(item, turnId).ConfigureAwait(false);
            else                 await DispatchStartAsync(item).ConfigureAwait(false);
        }
      } catch (Exception ex) {
        // The dispatch primitives handle their own errors; this only fires on an unexpected fault, and
        // must reset the guard so input dispatch can never wedge on a stuck _dispatching flag.
        _logger.LogError(ex, "codex app-server: input dispatch loop faulted unexpectedly.");
        lock (_gate) _dispatching = false;
      }
    }

    async Task DispatchStartAsync(InputItem item) {
        using var linked = LinkInputToken(item);
        try {
            var started = await _startTurn(item.Text, linked?.Token ?? _ct).ConfigureAwait(false);
            bool carried = false;
            lock (_gate) {
                // FaultAll may have run during the await; if so the item is already faulted and the
                // queue cleared — do NOT resurrect Active state or Dequeue an empty queue.
                if (!_faulted) {
                    _pending = Pending.None;
                    var alreadyDone = started.Status is not null and not "inProgress"
                        || string.Equals(_completedBeforeStartResp, started.TurnId, StringComparison.Ordinal);
                    _completedBeforeStartResp = null;
                    if (alreadyDone) {
                        _lifecycle    = Lifecycle.Idle;
                        _activeTurnId = null;
                    } else {
                        _lifecycle    = Lifecycle.Active;
                        _activeTurnId = started.TurnId;
                    }
                    _queue.Dequeue();
                    carried = true;
                }
            }
            if (carried) item.Ack.TrySetResult(); // the start carried this input (it was the turn's prompt)
        } catch (Exception ex) {
            FailHead(item, ex);
        }
        SignalTransitions();
    }

    async Task DispatchSteerAsync(InputItem item, string turnId) {
        using var linked = LinkInputToken(item);
        try {
            await _steerTurn(turnId, item.Text, linked?.Token ?? _ct).ConfigureAwait(false);
            bool carried = false;
            lock (_gate) {
                if (!_faulted) { _pending = Pending.None; _queue.Dequeue(); carried = true; }
            }
            if (carried) item.Ack.TrySetResult(); // accepted onto the active turn before it completed
        } catch (CodexAppServerRpcException rpc) when (rpc.Code == -32600 && !item.RetriedAsStart) {
            // The turn ended before the steer landed (spike Q13). Retry this same input EXACTLY ONCE
            // as a turn/start — force the lifecycle idle for the missed turn so the retry can't re-steer.
            lock (_gate) {
                if (!_faulted) {
                    item.RetriedAsStart = true;
                    _pending = Pending.None;
                    if (string.Equals(_activeTurnId, turnId, StringComparison.Ordinal)) {
                        _lifecycle    = Lifecycle.Idle;
                        _activeTurnId = null;
                    }
                }
            }
            _logger.LogDebug("codex app-server: steer missed turn {TurnId}; retrying the input as turn/start.", turnId);
        } catch (Exception ex) {
            FailHead(item, ex);
        }
        SignalTransitions();
    }

    async Task DispatchCompactAsync(InputItem item) {
        try {
            await _compact!(_ct).ConfigureAwait(false);
            bool carried = false, endedEarly = false;
            lock (_gate) {
                if (!_faulted) {
                    _pending   = Pending.None;
                    endedEarly = _compactionEndedEarly;
                    if (endedEarly) {
                        _lifecycle    = Lifecycle.Idle;
                        _activeTurnId = null;
                    } else {
                        _lifecycle    = Lifecycle.Compacting;
                        _activeTurnId = _compactionTurnId;
                        _compacting   = item;
                    }
                    _compactionTurnId     = null;
                    _compactionEndedEarly = false;
                    _queue.Dequeue();
                    carried = true;
                }
            }
            if (carried) {
                item.Ack.TrySetResult();
                if (endedEarly) item.Finished!.TrySetResult();
            }
        } catch (Exception ex) {
            lock (_gate) {
                _compactionTurnId     = null;
                _compactionEndedEarly = false;
            }
            FailHead(item, ex);
            item.Finished!.TrySetResult();
        }
        SignalTransitions();
    }

    // Links a per-input token (e.g. the launch's linked token on the initial prompt) with the
    // runtime-wide token; null when the input carried no cancellable token, so the caller uses _ct.
    CancellationTokenSource? LinkInputToken(InputItem item) =>
        item.Ct.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(_ct, item.Ct) : null;

    void FailHead(InputItem item, Exception ex) {
        lock (_gate) {
            _pending = Pending.None;
            if (_queue.Count > 0 && ReferenceEquals(_queue.Peek(), item)) _queue.Dequeue();
        }
        item.Ack.TrySetException(ex);
    }

    bool IsSettledLocked() => _lifecycle == Lifecycle.Idle && _pending == Pending.None && _queue.Count == 0;

    // In-flight spans from a turn/start being SENT (pending Start) through the active turn, matching the
    // old clock's turn/start-to-turn/completed window — a pending Steer implies Active, so it's covered.
    bool InFlightLocked() => _lifecycle != Lifecycle.Idle || _pending is Pending.Start or Pending.Compact;

    // Fires the in-flight callback on a real change and releases any settled waiters — both OUTSIDE the
    // lock so a callback can never re-enter the dispatcher under it.
    void SignalTransitions() {
        bool inFlight;
        bool fireInFlight = false;
        List<TaskCompletionSource>? settled = null;

        lock (_gate) {
            inFlight = InFlightLocked();
            if (inFlight != _lastInFlight) { _lastInFlight = inFlight; fireInFlight = true; }
            if (IsSettledLocked() && _settledWaiters.Count > 0) {
                settled = [.. _settledWaiters];
                _settledWaiters.Clear();
            }
        }

        // The in-flight callback is external (the clock); a throw from it must never propagate into the
        // dispatch loop and strand queued input.
        if (fireInFlight) {
            try { _onTurnInFlight?.Invoke(inFlight); }
            catch (Exception ex) { _logger.LogError(ex, "codex app-server: turn-in-flight callback threw."); }
        }
        if (settled is not null) foreach (var w in settled) w.TrySetResult();
    }

    enum Pending   { None, Start, Steer, Compact }
    enum Lifecycle { Idle, Active, Compacting }

    // An item is only ever touched by the single active dispatch loop (the _dispatching guard admits
    // exactly one), so its mutable RetriedAsStart needs no synchronization — the per-iteration lock
    // acquisitions fence the one write against the next iteration's read.
    sealed class InputItem(string text, CancellationToken ct, bool compaction = false) {
        public string Text { get; } = text;
        public CancellationToken Ct { get; } = ct;
        public bool Compaction { get; } = compaction;
        public TaskCompletionSource Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Finished { get; } = compaction ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        public bool RetriedAsStart { get; set; }
    }
}
