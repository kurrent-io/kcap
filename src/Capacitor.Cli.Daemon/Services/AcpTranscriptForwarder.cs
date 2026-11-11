using System.Buffers;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Resend from ExpectedNextSeq on a gap; otherwise discard buffered envelopes through AcceptedSeq.
/// A terminal binding drops new envelopes without advancing its cursor. A single-event drop is
/// recognizable directly, but a multi-event batch can report the dropped sequence as a gap.
/// Bound repeated no-progress resends so that ambiguous terminal state cannot loop forever.
/// Registration and binding must precede forwarding. The server owns session-end events.
/// A rebind supplies no SessionStarted and resumes numbering at the canonical AcceptedSeq + 1;
/// starting at zero would silently deduplicate new events.
/// </summary>
internal sealed class AcpTranscriptForwarder : IAcpPersistenceProbe {
    static readonly TimeSpan DefaultInitialSendRetryDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan DefaultMaxSendRetryDelay     = TimeSpan.FromSeconds(30);
    static readonly TimeSpan DefaultStalledGapResendDelay = TimeSpan.FromSeconds(1);
    const int                DefaultMaxStalledGapResends  = 5;

    // One invocation shares the daemon's single SignalR connection with DaemonPing, whose deadline is
    // 5 s: a multi-megabyte batch queues the ping behind it, the deadline forces a reconnect, and the
    // retry resends the same batch. Each batch stays under this size unless one envelope alone exceeds it.
    internal const int DefaultMaxBatchBytes = 256 * 1024;

    readonly Func<AcpEventEnvelope[], CancellationToken, Task<AcpBatchAck>> _send;
    readonly ChannelReader<AcpEventEnvelope>                                _envelopes;
    readonly ILogger                                                       _logger;
    readonly TimeProvider                                                  _time;
    readonly AcpEventEnvelope?                                             _initialEnvelope;
    readonly TimeSpan                                                      _initialSendRetryDelay;
    readonly TimeSpan                                                      _maxSendRetryDelay;
    readonly int                                                           _maxStalledGapResends;
    readonly TimeSpan                                                      _stalledGapResendDelay;
    readonly int                                                           _maxBatchBytes;

    /// <summary>Drained envelopes, seq already assigned, not yet sent.</summary>
    readonly Queue<AcpEventEnvelope> _pending = new();

    /// <summary>
    /// Retain every unacknowledged sequence until acceptance so retries can resend the exact same batch.
    /// Sorted ordering keeps gap recovery deterministic.
    /// </summary>
    readonly SortedDictionary<long, AcpEventEnvelope> _unacked = new();

    long _nextSeq     = 1; // seq 0 is reserved for the initial envelope
    long _highestSent = -1;

    // Hot-loop guard: the "already-terminal binding reporting an indistinguishable
    // gap" edge (see this class's remarks) would otherwise make the gap path resend forever from the
    // same ExpectedNextSeq. Track consecutive same-point resends that made no progress; cap them.
    long? _lastResendFrom;
    int   _stalledGapResends;

    /// <summary>
    /// Set once a terminal-drop ack stops the loop. Lets a caller distinguish "the transcript
    /// channel completed normally" from "the server-side binding went terminal out from under this
    /// forwarder".
    /// </summary>
    public bool IsTerminal { get; private set; }

    readonly long _firstCanonicalSeq;
    readonly TaskCompletionSource<bool> _firstAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True once the server has accepted the first canonical envelope; false when the forwarder
    /// stops first, a rejected or terminal binding among the reasons.</summary>
    public Task<bool> FirstEventAccepted => _firstAccepted.Task;

    const int MaxTrackedUserMessages = 64;

    // Read from the policy seam's thread, written by the loop: guarded by _persistence.
    readonly Lock                             _persistence     = new();
    readonly Dictionary<string, long>         _userMessageSeqs = new(StringComparer.Ordinal);
    readonly Queue<string>                    _userMessageOrder = new();
    long                                      _persistedSeq    = -1;
    bool                                      _stopped;
    TaskCompletionSource                      _progress        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IReadOnlyDictionary<string, long>> AwaitPersistedAsync(
            IReadOnlyCollection<string> itemIds, TimeSpan budget, CancellationToken ct) {
        var deadline = _time.GetUtcNow() + budget;

        while (true) {
            Task progress;
            Dictionary<string, long> stored;
            bool stopped;

            lock (_persistence) {
                stopped = _stopped;
                stored = itemIds
                    .Where(id => _userMessageSeqs.TryGetValue(id, out var seq) && seq <= _persistedSeq)
                    .Distinct(StringComparer.Ordinal)
                    .ToDictionary(id => id, id => _userMessageSeqs[id], StringComparer.Ordinal);
                progress = _progress.Task;
            }

            var left = deadline - _time.GetUtcNow();
            if (stored.Count == itemIds.Distinct(StringComparer.Ordinal).Count() || left <= TimeSpan.Zero || stopped) return stored;

            try {
                await progress.WaitAsync(left, _time, ct).ConfigureAwait(false);
            } catch (TimeoutException) {
                // The next pass returns what is stored by now.
            }
        }
    }

    void RecordUserMessage(AcpEventEnvelope envelope) {
        if (envelope is not { Kind: AcpEventKind.UserMessage, Ephemeral: false, ItemId: { Length: > 0 } itemId }) return;

        lock (_persistence) {
            if (_userMessageSeqs.TryAdd(itemId, envelope.Seq)) _userMessageOrder.Enqueue(itemId);
            if (_userMessageOrder.Count > MaxTrackedUserMessages) _userMessageSeqs.Remove(_userMessageOrder.Dequeue());
            Signal();
        }
    }

    void RecordPersisted(long persistedSeq) {
        lock (_persistence) {
            if (persistedSeq > _persistedSeq) _persistedSeq = persistedSeq;
            Signal();
        }
    }

    void Signal() {
        var reached = _progress;
        _progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        reached.TrySetResult();
    }

    /// <summary>Test-only visibility into the unacked buffer's current size.</summary>
    internal int UnackedCount => _unacked.Count;

    /// <summary>
    /// <paramref name="initialSendRetryDelay"/>/<paramref name="maxSendRetryDelay"/> default to 1s/30s
    /// (production) but are overridable so unit tests can exercise <see cref="SendWithRetryAsync"/>'s
    /// backoff without real sleeping.
    /// </summary>
    /// <param name="resumeFromSeq">
    /// The canonical AcceptedSeq this forwarder resumes from. When set, do NOT
    /// (re)send SessionStarted and number the first new event at <paramref name="resumeFromSeq"/>+1, so a
    /// resumed incarnation's events land at the next canonical sequence instead of being deduped against
    /// the prior incarnation's cursor. Null = a fresh session (SessionStarted@0, today's behaviour).
    /// </param>
    public AcpTranscriptForwarder(
            Func<AcpEventEnvelope[], CancellationToken, Task<AcpBatchAck>> send,
            AcpEventEnvelope?                                              initialEnvelope,
            ChannelReader<AcpEventEnvelope>                                envelopes,
            ILogger                                                        logger,
            TimeProvider                                                   time,
            TimeSpan?                                                      initialSendRetryDelay = null,
            TimeSpan?                                                      maxSendRetryDelay = null,
            int?                                                           maxStalledGapResends = null,
            TimeSpan?                                                      stalledGapResendDelay = null,
            long?                                                          resumeFromSeq = null,
            int?                                                           maxBatchBytes = null
        ) {
        _send                   = send;
        _envelopes              = envelopes;
        _logger                 = logger;
        _time                   = time;
        if (resumeFromSeq is { } rs) {
            // Resume/rebind: canonical already holds events through rs. No SessionStarted (the source-claim
            // rebind is idempotent by thread id), and new events start at rs+1.
            _initialEnvelope = null;
            _nextSeq         = rs + 1;
            _highestSent     = rs;
        } else {
            _initialEnvelope = (initialEnvelope ?? throw new ArgumentNullException(nameof(initialEnvelope))) with { Seq = 0 };
        }
        _firstCanonicalSeq      = _initialEnvelope is null ? _nextSeq : 0;
        _initialSendRetryDelay  = initialSendRetryDelay ?? DefaultInitialSendRetryDelay;
        _maxSendRetryDelay      = maxSendRetryDelay ?? DefaultMaxSendRetryDelay;
        _maxStalledGapResends   = maxStalledGapResends ?? DefaultMaxStalledGapResends;
        _stalledGapResendDelay  = stalledGapResendDelay ?? DefaultStalledGapResendDelay;
        _maxBatchBytes          = maxBatchBytes ?? DefaultMaxBatchBytes;
    }

    /// <summary>
    /// Runs the forward loop until the transcript channel completes and every envelope has been
    /// acked, a terminal-drop ack stops it early (<see cref="IsTerminal"/> flips true), or
    /// <paramref name="ct"/> is cancelled (returns promptly — no hang; a cancellation is swallowed
    /// here rather than propagated, mirroring <c>AcpHostedAgentRuntime.RunTurnWorkerAsync</c>'s
    /// "normal shutdown" convention so a fire-and-forget caller doesn't need its own try/catch).
    ///
    /// Single-in-flight: exactly one <c>send</c> call is outstanding at a time — a batch is sent,
    /// its ack is fully processed, and only then is the next batch decided (either a gap resend from
    /// the buffer, or a fresh opportunistic drain of whatever the channel has ready). This keeps the
    /// seq/ack bookkeeping trivially race-free without needing its own locking.
    /// </summary>
    public async Task RunAsync(CancellationToken ct) {
        try {
            // Resume mode (_initialEnvelope null) skips the initial-send branch entirely and drains new
            // envelopes straight from _nextSeq (== resumeFromSeq+1); a fresh session sends SessionStarted@0.
            var    pendingInitial = _initialEnvelope is not null;
            long?  resendFrom     = null;

            while (true) {
                ct.ThrowIfCancellationRequested();

                AcpEventEnvelope[] batch;

                if (resendFrom is { } from) {
                    batch = _unacked.Where(kv => kv.Key >= from).Select(kv => kv.Value).ToArray();

                    if (batch.Length == 0) {
                        // Defensive only — a gap's ExpectedNextSeq should always point at something
                        // still in the buffer (see this class's remarks); if it somehow doesn't,
                        // drop the stale cursor and fall through to draining fresh envelopes rather
                        // than spinning here.
                        resendFrom = null;
                        continue;
                    }
                } else if (pendingInitial) {
                    pendingInitial = false;
                    // Only reachable when pendingInitial started true, i.e. _initialEnvelope is non-null
                    // (fresh session); resume mode never sets pendingInitial and so never dereferences it.
                    var initial = _initialEnvelope!.Value;
                    _unacked[initial.Seq] = initial;
                    _highestSent = initial.Seq;
                    batch = [initial];
                } else {
                    if (_pending.Count == 0) {
                        // Channel completed. The unacked buffer is always empty here: we only ever reach
                        // this branch right after a normal ack removed everything ≤ AcceptedSeq.
                        if (await DrainNewEnvelopesAsync(ct).ConfigureAwait(false) is not { } drained)
                            return;

                        foreach (var envelope in drained) _pending.Enqueue(envelope);
                    }

                    batch = DequeuePendingBatch();

                    // Canonical-only bookkeeping. Ephemerals are fire-and-forget: not retained for
                    // resend, and not counted in the high-water mark the ack is compared against. An
                    // all-ephemeral batch therefore leaves _highestSent untouched, so the ack that
                    // follows cannot look like a terminal-drop.
                    foreach (var envelope in batch.Where(static e => !e.Ephemeral)) {
                        _unacked[envelope.Seq] = envelope;
                        _highestSent           = envelope.Seq;
                    }
                }

                if (batch.Length == 0) continue; // every pending envelope was an oversized ephemeral

                var ack = await SendWithRetryAsync(batch, ct).ConfigureAwait(false);

                // the server explicitly rejected this binding (stale/foreign/unbound). Stop
                // BEFORE interpreting the gap/cursor fields — the rejection ack deliberately carries a
                // -1 AcceptedSeq that would otherwise read as a terminal-drop anyway, but checking the
                // flag first keeps the intent explicit. An old server never sets this; that path still
                // stops via the AcceptedSeq < _highestSent terminal-drop below.
                if (ack.Rejected) {
                    LogRejectedBinding();
                    _unacked.Clear();
                    IsTerminal = true;

                    return;
                }

                if (ack.ExpectedNextSeq is { } expectedNextSeq) {
                    // A legitimate gap resolves on the first resend (the server wanted an earlier seq,
                    // then catches up — a NEW ExpectedNextSeq each round = progress). But the server's
                    // per-envelope loop makes an ALREADY-terminal binding report a dropped envelope as
                    // a gap indistinguishable from a real one (this class's "Known edge case"): the
                    // SAME ExpectedNextSeq comes back with no progress, and a bare `continue` here would
                    // hot-spin forever. Guard it: cap consecutive same-point resends (with a small
                    // backoff), then treat the binding as terminal — stop + clear rather than hammer.
                    if (expectedNextSeq == _lastResendFrom) {
                        if (++_stalledGapResends >= _maxStalledGapResends) {
                            LogGapStall(expectedNextSeq, _stalledGapResends);
                            _unacked.Clear();
                            IsTerminal = true;
                            return;
                        }

                        await Task.Delay(_stalledGapResendDelay, _time, ct).ConfigureAwait(false);
                    } else {
                        _lastResendFrom    = expectedNextSeq;
                        _stalledGapResends = 0;
                    }

                    resendFrom = expectedNextSeq;
                    continue;
                }

                resendFrom         = null;
                _lastResendFrom    = null;
                _stalledGapResends = 0;

                if (ack.AcceptedSeq < _highestSent) {
                    LogTerminalDrop(ack.AcceptedSeq, _highestSent);
                    _unacked.Clear();
                    IsTerminal = true;
                    return;
                }

                foreach (var seq in _unacked.Keys.Where(seq => seq <= ack.AcceptedSeq).ToArray())
                    _unacked.Remove(seq);

                RecordPersisted(ack.PersistedSeq);

                if (ack.AcceptedSeq >= _firstCanonicalSeq) _firstAccepted.TrySetResult(true);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // normal shutdown — see this method's remarks.
        } finally {
            _firstAccepted.TrySetResult(false);
            // A stopped forwarder stores nothing more; a waiter returns what is stored now.
            lock (_persistence) {
                _stopped = true;
                Signal();
            }
        }
    }

    /// <summary>
    /// Batch currently available envelopes without waiting for a full batch; preserve sequence order
    /// and the configured resource bounds.
    /// </summary>
    async Task<AcpEventEnvelope[]?> DrainNewEnvelopesAsync(CancellationToken ct) {
        if (!await _envelopes.WaitToReadAsync(ct).ConfigureAwait(false))
            return null;

        List<AcpEventEnvelope>? batch = null;

        while (_envelopes.TryRead(out var raw)) {
            var envelope = raw.Ephemeral ? raw : raw with { Seq = _nextSeq++ };
            RecordUserMessage(envelope);
            (batch ??= []).Add(envelope);
        }

        return batch is null ? null : DropSupersededEphemerals(batch);
    }

    /// <summary>
    /// An ephemeral carries its item's whole content so far and a viewer keeps only the latest one, so
    /// one followed in the same drain by a newer ephemeral of the same item and kind, or by any
    /// canonical envelope of that item, is never sent. A long command's output would otherwise ride
    /// once per delta, each copy as large as everything before it.
    /// </summary>
    internal static AcpEventEnvelope[] DropSupersededEphemerals(List<AcpEventEnvelope> drained) {
        var kept         = new List<AcpEventEnvelope>(drained.Count);
        var laterAny     = new HashSet<string>(StringComparer.Ordinal);
        var laterOfKind  = new HashSet<(string, string)>();

        for (var i = drained.Count - 1; i >= 0; i--) {
            var e = drained[i];

            if (e.ItemId is not { } item) {
                kept.Add(e);
                continue;
            }

            if (e.Ephemeral) {
                if (laterAny.Contains(item) || !laterOfKind.Add((item, e.Kind))) continue;
            } else {
                laterAny.Add(item);
            }

            kept.Add(e);
        }

        kept.Reverse();

        return [.. kept];
    }

    /// <summary>
    /// The next pending batch within <see cref="_maxBatchBytes"/>, at least one envelope. An ephemeral
    /// over the budget is dropped rather than sent: it is never persisted, and its item's canonical
    /// envelope delivers the content.
    /// </summary>
    AcpEventEnvelope[] DequeuePendingBatch() {
        var  batch = new List<AcpEventEnvelope>();
        long bytes = 0;

        while (_pending.TryPeek(out var next)) {
            var size = EstimateBytes(next);

            if (next.Ephemeral && size > _maxBatchBytes) {
                _pending.Dequeue();
                continue;
            }

            if (batch.Count > 0 && bytes + size > _maxBatchBytes) break;

            batch.Add(_pending.Dequeue());
            bytes += size;
        }

        return [.. batch];
    }

    /// <summary>An upper bound on the envelope's serialized size: every string at its escaped length,
    /// plus a fixed allowance for the property names and scalar fields.</summary>
    internal static long EstimateBytes(in AcpEventEnvelope e) =>
        1024L
      + EscapedBytes(e.Text) + EscapedBytes(e.ToolInputJson) + EscapedBytes(e.ToolResult)
      + EscapedBytes(e.ToolName) + EscapedBytes(e.ToolCallId) + EscapedBytes(e.ToolKind) + EscapedBytes(e.ItemId)
      + EscapedBytes(e.Cwd) + EscapedBytes(e.Model) + EscapedBytes(e.RawSessionId) + EscapedBytes(e.SessionMode)
      + EscapedBytes(e.EndReason) + EscapedBytes(e.TimestampIso) + EscapedBytes(e.Kind)
      + EscapedBytes(e.RepoOwner) + EscapedBytes(e.RepoName) + EscapedBytes(e.RepoRemoteUrl) + EscapedBytes(e.RepoBranch)
      + EscapedBytes(e.AgentId) + EscapedBytes(e.AgentType) + EscapedBytes(e.TurnId);

    // The hub's serializer uses the default encoder, which writes every non-ASCII and HTML-sensitive
    // character as a six-byte \uXXXX escape; counting characters would let such text blow the budget.
    static long EscapedBytes(string? s) {
        if (s is null) return 0;

        var safe = s.AsSpan().IndexOfAnyExcept(PlainJsonChars);
        if (safe < 0) return s.Length;

        long bytes = safe;
        foreach (var c in s.AsSpan(safe)) bytes += PlainJsonChars.Contains(c) ? 1 : 6;

        return bytes;
    }

    static readonly SearchValues<char> PlainJsonChars = SearchValues.Create(
        " !#$%()*,-./0123456789:;=?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_abcdefghijklmnopqrstuvwxyz{|}~");

    /// <summary>
    /// Retry the same batch on send failures without advancing its sequence cursor; server deduplication
    /// makes an already persisted resend safe. The delegate handles connection readiness, while this outer
    /// backoff covers faults escaping it. Caller cancellation alone terminates these retries.
    /// </summary>
    async Task<AcpBatchAck> SendWithRetryAsync(AcpEventEnvelope[] batch, CancellationToken ct) {
        var delay = _initialSendRetryDelay;

        while (true) {
            try {
                return await _send(batch, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                LogSendFailed(ex, batch[0].Seq, batch[^1].Seq, delay);

                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _maxSendRetryDelay.TotalMilliseconds));
            }
        }
    }

    void LogGapStall(long expectedNextSeq, int attempts) =>
        _logger.LogWarning(
            "ACP transcript forwarder: gap ack stalled at ExpectedNextSeq={ExpectedNextSeq} for {Attempts} consecutive no-progress resends — treating the binding as terminal and stopping (likely an already-terminal binding reporting an indistinguishable gap; see AcpTranscriptForwarder remarks).",
            expectedNextSeq, attempts);

    void LogTerminalDrop(long acceptedSeq, long highestSent) =>
        _logger.LogWarning(
            "ACP transcript forwarder: terminal-drop ack (AcceptedSeq={AcceptedSeq} < highest-sent={HighestSent}) — the server-side binding is terminal; stopping.",
            acceptedSeq, highestSent);

    void LogRejectedBinding() =>
        _logger.LogInformation(
            "ACP transcript forwarder: server returned a rejection ack (stale/foreign/unbound binding) — stopping.");

    void LogSendFailed(Exception ex, long firstSeq, long lastSeq, TimeSpan delay) =>
        _logger.LogDebug(
            ex,
            "ACP transcript forwarder: send failed for seq [{FirstSeq}..{LastSeq}]; retrying in {DelayMs}ms.",
            firstSeq, lastSeq, delay.TotalMilliseconds);
}
