using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.RepoEvidence;
using Capacitor.Cli.Recording.Capture;
using Capacitor.Cli.Recording.Harness.Antigravity;
using Capacitor.Cli.Recording.Harness.Copilot;
using Capacitor.Cli.Recording.Harness.Cursor;
using Capacitor.Cli.Recording.Harness.Kiro;

namespace Capacitor.Cli.Recording;

/// <summary>
/// Sends one transcript to the server as it grows. Everything it mutates is its own, because many
/// recorders can share a process, and it leaves the process itself alone: no console, signals, pid
/// file or exit code. <see cref="RunAsync"/> tails until stopped, and <see cref="FinishAsync"/> then
/// makes the final drain. Given <paramref name="claims"/>, it records only while its source's claim exists;
/// beating that claim is its host's job.
/// </summary>
public sealed class TranscriptRecorder(
        RecordingSource source, IRecorderTransport transport, IRowStamper stamper, TimeProvider time, RecorderContext context,
        RecorderClaims? claims = null) : IDisposable {
    readonly CursorMarkers  _markers       = new(context.Server.Config, time) { FailureLog = context.Log };
    readonly WatchAckCursor _ackCursor     = new(context.Server.Config);
    readonly TranscriptSpool _spool        = new(context.Server.Config, time, context.Server.Url) { FailureLog = context.Log };
    readonly Lock           _ackCursorGate = new();
    int _ackCursorWritten = -1;

    readonly OnceLog _parseErrors  = new(context.Log);
    readonly OnceLog _kiroWarnings = new(context.Log);

    // The rewrite guard's checkpoint and pending range persist from poll to poll. Only Cursor has one.
    readonly CursorRewriteGuard? _cursorGuard = source.Vendor == "cursor" ? new(context.Server.Config, source.SessionId, time, context.Log) : null;

    // Serializes a reconnect rewind against a drain: both move the byte and line frontiers and the
    // guard's checkpoint, and the transport can report a reconnect while the loop is mid-drain.
    readonly SemaphoreSlim? _cursorRewindGate = source.Vendor == "cursor" ? new(1, 1) : null;

    readonly CancellationTokenSource _stop     = new();
    readonly CancellationTokenSource _titleCts = new();

    long _shutdownRequestedAt;
    int  _stopRequested;
    bool _subscribed;

    // A moved file keeps its lines, so the drain carries on from the line it had reached. A missing
    // file this recorder has never seen, at line 0, is one Claude has not written yet; the cursor
    // alone cannot tell, since it stays at 0 while lines are buffered below the threshold.
    bool _transcriptSeen;

    /// <summary>Longest single wait between beats, well inside the staleness threshold, so no wait broken into
    /// slices of this size can make a live recorder look wedged.</summary>
    public static readonly TimeSpan HeartbeatSlice = TimeSpan.FromSeconds(5);

    /// <summary>Every Nth poll, roughly once a minute, the whole file is hashed again, so a rewrite inside a
    /// region the per-poll zone checks have moved past is still caught.</summary>
    internal const int CursorFullPrefixVerifyEveryNPolls = 60;

    internal static readonly TimeSpan DrainOnceConnectBudget = TimeSpan.FromSeconds(30);

    static readonly TimeSpan DrainCompleteBudget = TimeSpan.FromSeconds(5);

    // Inside the 5s a stop request allows before the kill, so the spool append or the needs-import
    // marker is written before the process can be killed.
    static readonly TimeSpan ShutdownTailRedactionDeadline = TimeSpan.FromSeconds(4);
    static readonly TimeSpan MinShutdownTailRedaction      = TimeSpan.FromSeconds(1);

    // From the stop request, which a kill follows 5s later; the rest covers the drain-complete signal and the close.
    static readonly TimeSpan ShutdownTitleDeadline = TimeSpan.FromSeconds(4);

    // Nothing kills the recorder on any other exit; the title CLI's own timeout (15s claude, 30s codex) ends it first.
    static readonly TimeSpan UnhurriedShutdownTitleDeadline = TimeSpan.FromSeconds(30);

    /// <summary>How a poll opens the transcript; null opens the file. A test counts the bytes a poll reads through it.</summary>
    internal Func<string, FileStream>? OpenTranscript { get; init; }

    /// <summary>The server has registered this recorder, so what it holds is the server's to resume.</summary>
    public bool Connected { get; private set; }

    public WatchState State { get; } = new() { LastActivityAt = time.GetUtcNow() };

    /// <summary>Cancelled the moment the recorder stops, by any cause and before its loop notices, which can be
    /// long after: it may be in a call that takes no token. A host stops what it runs beside the recorder from
    /// this, so nothing of the host's outlives the stop and changes how the session ended.</summary>
    public CancellationToken Stopping => _stop.Token;

    /// <summary>Where the transcript is now: Claude can move it mid-session.</summary>
    public string TranscriptPath { get; private set; } = source.TranscriptPath;

    /// <summary>The transcript went idle and the recorder ended its source.</summary>
    public bool EndedIdle { get; private set; }

    /// <summary>A connect given up on, which can still reach the server after this recorder has gone.</summary>
    internal Task? AbandonedConnect { get; private set; }

    /// <summary>A Copilot <c>session.shutdown</c> stayed the transcript's last line.</summary>
    public bool EndedOnCopilotShutdown { get; private set; }

    /// <summary>The claim this recorder ran under has gone. A hook took the source, and whatever recorder it started
    /// resumes from the server's cursor, so nothing more goes out from here, a final drain included.</summary>
    public bool LostClaim { get; private set; }

    internal HeldLineRedaction HeldLine { get; } =
        new(new HeldLineStore(context.Server.Config, source.SessionId, source.AgentId), time, context.RedactionClock, context.Log);

    /// <summary>Stops the loop for a sender that will not wait long: a stop request is followed by a kill within
    /// seconds, so <see cref="FinishAsync"/> spends less on a title still being generated.</summary>
    public void RequestStop() {
        Interlocked.Exchange(ref _stopRequested, 1);
        _stop.Cancel();
    }

    public void Dispose() {
        Unsubscribe();
        _stop.Dispose();

        // A title still being generated belongs to this attempt, and a retry makes its own.
        try {
            _titleCts.Cancel();
        } catch (ObjectDisposedException) {
            // Disposed twice.
        }

        _titleCts.Dispose();
        _cursorRewindGate?.Dispose();
    }

    /// <summary>Tails the transcript until <paramref name="ct"/> or the recorder itself stops it. A failure to
    /// register with the server is thrown, as is a failure of a vendor step between drains.</summary>
    public async Task RunAsync(CancellationToken ct) {
        using var callerStop = ct.Register(static me => ((TranscriptRecorder)me!).CancelRun(), this);

        var stop      = _stop.Token;
        var sessionId = source.SessionId;
        var agentId   = source.AgentId;
        var vendor    = source.Vendor;
        var log       = context.Log;

        // The kill grace runs from the stop request, not from the loop noticing it: an await the loop was in
        // when the request arrived has already spent some of it.
        using var shutdownStamp = stop.Register(() => Interlocked.CompareExchange(ref _shutdownRequestedAt, time.GetTimestamp(), 0));

        // Registering replaces whichever connection holds the source, and that may be the watcher that took it.
        if (HasLostClaim()) return;

        Subscribe();

        var idleTimeout = IdleEnd.ResolveIdleWindow(vendor, isSessionWatcher: agentId is null, Environment.GetEnvironmentVariable);

        await ConnectAndSeedAsync(stop);

        try {
            while (!stop.IsCancellationRequested) {
                // Every iteration, disconnected and idle ones included, so a stale heartbeat means the loop is
                // wedged, not merely quiet.
                context.Beat();

                if (HasLostClaim()) break;

                // No point re-reading the file or attempting sends that will fail; the transport reconnects.
                if (!transport.IsConnected) {
                    // Freeze the idle clock: the reconnect path subtracts the outage from the idle measure.
                    State.DisconnectedSince ??= time.GetUtcNow();

                    try {
                        await Task.Delay(TimeSpan.FromSeconds(5), time, stop);
                    } catch (OperationCanceledException) {
                        break;
                    }

                    continue;
                }

                if (State.DisconnectedSince is { } since) {
                    State.AccumulatedDisconnected += time.GetUtcNow() - since;
                    State.DisconnectedSince        = null;
                }

                await context.Housekeeping.BeforeDrainAsync(State, stop);

                FollowMovedTranscript();

                var drained = await DrainNewLinesGatedAsync(isFinalDrain: false, stop);

                await context.Housekeeping.AfterDrainAsync(State, TranscriptPath, drained, stop);

                // Keyed on the child's own session id for a child watcher: each Cursor hook touches its own raw
                // session id, and sessionId here is the parent's.
                var cursorIdleClockAt = IdleEnd.ResolveCursorIdleClock(
                    vendor, State.LastActivityAt,
                    vendor == "cursor" ? WatcherHeartbeat.Read(_markers.HeartbeatPath(agentId ?? sessionId)) : null);

                if (IdleEnd.ShouldEndOnIdle(
                        vendor,
                        isSessionWatcher: agentId is null,
                        State.ThresholdReached,
                        cursorIdleClockAt,
                        time.GetUtcNow(),
                        idleTimeout,
                        // A tool awaiting its result suppresses idle-end: Codex tracks call_ids,
                        // Antigravity counts PLANNER_RESPONSE calls vs result steps, Claude tracks
                        // tool_use ids awaiting their tool_result, Copilot Local tracks toolCallIds.
                        toolInFlight: vendor switch {
                            "antigravity" => State.PendingAntigravityToolCalls > 0,
                            "claude"      => State.PendingClaudeToolCalls.Count > 0,
                            "copilot"     => ToolCallTracking.CopilotToolInFlight(State, time.GetUtcNow()),
                            _             => State.PendingCodexToolCalls.Count > 0,
                        },
                        disconnectedSinceActivity: State.AccumulatedDisconnected,
                        noEndSignal: vendor == "copilot" && (State.CopilotHostMissing || CopilotPaths.IsVsCodeLocalTranscript(TranscriptPath)))) {
                    log($"{vendor} transcript idle for >{idleTimeout.TotalMinutes:F0}m; ending session (idle_timeout)");
                    EndedIdle = true;
                    _stop.Cancel();

                    break;
                }

                if (CopilotShutdown.CopilotShutdownSettled(State.CopilotShutdownSeenAt, time.GetUtcNow())) {
                    log($"session.shutdown still the transcript's last line after {CopilotShutdown.CopilotShutdownGrace.TotalSeconds:0}s; ending session (session_shutdown)");
                    EndedOnCopilotShutdown = true;
                    _stop.Cancel();

                    break;
                }

                try {
                    await Task.Delay(TimeSpan.FromSeconds(1), time, stop);
                } catch (OperationCanceledException) {
                    break;
                }
            }
        } catch (OperationCanceledException) {
            // Expected
        }
    }

    /// <summary>Registers, then makes the final drain, without tailing. For a source whose run never connected: the
    /// server's cursor says what is left, and what cannot be sent is spooled as <see cref="FinishAsync"/> does.
    /// Registering gets <see cref="DrainOnceConnectBudget"/>, then throws <see cref="TimeoutException"/>, because a
    /// transport that retries until it connects would otherwise park the drain with nothing spooled or marked.</summary>
    public async Task DrainOnceAsync(CancellationToken ct) {
        if (HasLostClaim()) return;

        Subscribe();
        await ConnectAndSeedAsync(ct, DrainOnceConnectBudget);
        await FinishAsync(ct);
    }

    /// <summary>Makes the final drain, whatever stopped the loop, and tells the server it is done. Lines it could
    /// not deliver go to the spool, because the process may exit right after. <paramref name="ct"/> cuts off what
    /// depends on the server and on a title, a hung send above all, and the spool still gets what is left.</summary>
    public async Task FinishAsync(CancellationToken ct) {
        // A stop can end the loop before a tick saw the claim go.
        if (HasLostClaim()) {
            Unsubscribe();

            return;
        }

        var agentId = source.AgentId;
        var vendor  = source.Vendor;
        var log     = context.Log;

        var shutdownStarted = Volatile.Read(ref _shutdownRequestedAt) is var requestedAt and not 0 ? requestedAt : time.GetTimestamp();

        // A held line stops the buffer short of the threshold however long the session is, so it
        // must not read as a short session whose lines are dropped.
        if (agentId is null && !State.ThresholdReached && HeldLine.Held is not null) {
            State.ThresholdReached = true;
            State.BufferedLines.Clear();
            State.BufferedLineNumbers.Clear();
        }

        FollowMovedTranscript();

        // The turn may have landed after the last poll, so the unsent lines are read again here.
        if (agentId is null && !State.ThresholdReached
         && TranscriptLineText.UnsentLinesHoldRealTurn(TranscriptPath, State.LinesProcessed, vendor, _parseErrors)) {
            log("Session ended below the buffering threshold with a real turn; draining it");
            State.ThresholdReached = true;
            State.BufferedLines.Clear();
            State.BufferedLineNumbers.Clear();

            if (State is { InitialTitleSent: false, FirstUserText: not null }) {
                State.InitialTitleSent = true;
                _                      = SendInitialTitleAsync(source.SessionId, TruncateForTitle(State.FirstUserText, 80));
            }
        }

        if (agentId is null && !State.ThresholdReached) {
            // No turn means no lines to send. The session still ends, through its host or the vendor's own end hook.
            log($"Session ended with no real turn ({State.BufferedLines.Count} line(s) buffered); not sending its lines");
        } else {
            log("Draining remaining lines...");

            // Give the writer up to 2s to finish its last record. That is best-effort: the drain below re-decides
            // completeness on the exact bytes it consumes, so a line that resumed growing is still held.
            var lastLineComplete = await TranscriptReader.WaitForFinalLineCompletionAsync(TranscriptPath, time, ct: ct);
            FollowMovedTranscript();

            IReadOnlyList<string> finalDrained = [];

            try {
                finalDrained = await DrainNewLinesGatedAsync(isFinalDrain: true, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                // Cut off behind a reconnect's rewind. The spool below takes the lines.
            }

            // A drain cut off before its read can't say, and the tail spool below leaves such a line out.
            if (State.FinalDrainHeldIncompleteLine || (ct.IsCancellationRequested && !lastLineComplete)) {
                // Keyed on the session even for a subagent: the spool and `kcap import` work at session level.
                log("Final transcript line still incomplete at consume time; held back and flagging needs-import");
                MarkForImport(_spool, "shutdown final drain: last transcript line never completed (no newline, unparseable)");
            }

            // The cursor only moves past lines the server confirmed, and a failed send can leave the connection
            // up, so this runs whatever the connection state: a cheap no-op when nothing is undelivered. It is the
            // last chance to act before the process exits, and spooling keeps the tail for a later drain to replay.
            FollowMovedTranscript();
            await SpoolUndeliveredTranscriptTailAsync(
                _spool, State.LinesProcessed, State.Commits, CancellationToken.None,
                ShutdownTailRedactionDeadline - time.GetElapsedTime(shutdownStarted), State.LineOffsets);

            try {
                await context.Housekeeping.AfterFinalDrainAsync(State, finalDrained, shutdownStarted, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                log("Final housekeeping cut off");
            }
        }

        // The generated title goes out over this connection, so it is waited for before the close.
        if (State is { PendingTitle: { } pendingTitle, TitleGenerated: false }) {
            // Read once: a connection reconnecting now can be back by the hand-off check, too late for a title that failed.
            var connected = transport.IsConnected;
            var settled   = pendingTitle.IsCompleted;
            if (!settled) {
                var titleBudget = ShutdownTitleBudget(connected, Volatile.Read(ref _stopRequested) == 1, time.GetElapsedTime(shutdownStarted));
                log($"Waiting up to {Math.Max(0, titleBudget.TotalSeconds):F1}s for the generated title before closing the connection");

                settled = await AwaitPendingTitleAsync(pendingTitle, titleBudget, time, context.Beat, ct);
                if (!settled) await _titleCts.CancelAsync();
            }

            // A kill follows a stop request within seconds and a title takes longer, and a closed connection cannot
            // carry one at all, so another process finishes it over HTTP, as it does one that never reached the server.
            if (!State.TitleGenerated && (Volatile.Read(ref _stopRequested) == 1 || ct.IsCancellationRequested || !connected || !transport.IsConnected)) {
                log($"Generated title {(settled ? "not sent" : "not ready")} before shutdown; handing it to a title generator");
                context.Housekeeping.HandOffTitle(TranscriptPath);
            } else if (!settled) {
                log("Generated title not ready before shutdown; abandoning it");
            }
        }

        // A fresh token: the run's is already cancelled, and the server has to hear this to release a stop
        // waiting on the drain.
        try {
            if (transport.IsConnected) {
                using var budget          = new CancellationTokenSource(DrainCompleteBudget, time);
                using var drainCompleteCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
                await transport.DrainCompleteAsync(source, drainCompleteCts.Token);
                log("Drain complete signaled to server");
            }
        } catch (Exception ex) {
            log($"Failed to signal drain complete: {ex.Message}");
        }

        Unsubscribe();
    }

    /// <summary>Deletes the published ack count. Call it once the recorder is done, so the count can't outlive it.</summary>
    public void RetireAckCursor() {
        if (source.AgentId is null && source.Vendor == "codex") _ackCursor.Delete(source.SessionId);
    }

    /// <summary>The Codex hook declares only the turns the server acknowledged, so the root recorder publishes its
    /// count whenever it moves, rewinds included. Reconnect and drain both publish, so the count is read and
    /// written under one lock: a count read earlier can never land after a newer one.</summary>
    internal void PublishAckedLines(WatchState state) {
        if (source.AgentId is not null || source.Vendor != "codex") return;

        lock (_ackCursorGate) {
            var count = state.LinesProcessed;
            if (count == _ackCursorWritten) return;
            if (_ackCursor.Write(source.SessionId, count)) _ackCursorWritten = count;
        }
    }

    /// <summary>Registers with the server and rebuilds what the lines before its cursor fed. Shared by the tailing
    /// run and the single drain, so both start from the same state.</summary>
    async Task ConnectAndSeedAsync(CancellationToken stop, TimeSpan? connectBudget = null) {
        var sessionId = source.SessionId;
        var agentId   = source.AgentId;
        var vendor    = source.Vendor;
        var log       = context.Log;

        State.TitleCancellation = _titleCts.Token;

        // These vendors stream from the first line, and their session watchers are eligible for the
        // idle ceiling from the start, which requires the threshold. A no-op for child watchers.
        if (SkipsThresholdBuffering(vendor)) State.ThresholdReached = true;
        State.StartedAt = time.GetUtcNow();

        await context.Housekeeping.PrepareAsync(State, stop);

        log($"Watching {TranscriptPath} for session {sessionId}" + (agentId is not null ? $" agent {agentId}" : ""));

        State.LinesProcessed = await ConnectAsync(stop, connectBudget);
        Connected            = true;
        // Lines the server already holds were flushed by an earlier watcher, so the tail is never held back.
        if (State.LinesProcessed > 0) State.ThresholdReached = true;
        PublishAckedLines(State);
        log($"Connected via SignalR, resuming from line {State.LinesProcessed}");
        context.Beat();

        // A resumed session can be told a path its transcript has already left; the seed and replay read it next.
        FollowMovedTranscript();

        context.Housekeeping.OnConnected();

        // The server's frontier can exceed the local line count when the transcript was replaced while nothing
        // watched it. That is quarantined rather than seeded, and the loop below never runs.
        if (!await SeedCursorByteOffsetAsync(State, State.LinesProcessed, _cursorGuard, stop)) {
            log("cursor_transcript_rewrite_detected: initial resume frontier beyond local transcript — quarantined, exiting");
            _stop.Cancel();
        }

        // Resumed mid-session: rebuild what the lines before the cursor would have fed, such as a tool
        // that opened before it, which idle-end would otherwise mistake for an idle agent. At line 0 the
        // drain delivers the whole file and feeds every consumer itself.
        if (State.LinesProcessed > 0) {
            var replay = new List<TranscriptReplayConsumer>();
            if (State.SecondaryRoots is { } secondaryRoots) replay.Add(SecondaryRootsReplay(secondaryRoots, vendor));
            if (vendor == "claude") replay.Add(CreatedPullRequestsReplay(State));
            if (ToolCallTracking.TracksClaudeToolCalls(vendor, isSessionWatcher: agentId is null)) replay.Add(ToolCallTracking.ClaudePendingToolCallsReplay(State.PendingClaudeToolCalls));
            if (ToolCallTracking.TracksCodexToolCalls(vendor)) replay.Add(ToolCallTracking.CodexWatcherStateReplay(State, isChildWatcher: agentId is not null));
            if (ToolCallTracking.TracksCopilotToolCalls(vendor, TranscriptPath, State.CopilotHostMissing)) replay.Add(new(WindowOnly: false, line => { ToolCallTracking.UpdateCopilotPendingToolCalls(State.PendingCopilotToolCalls, line); return false; }));
            await TranscriptReplay.ReplayAsync(TranscriptPath, State.LinesProcessed, replay, stop, log);
        }
    }

    async Task<int> ConnectAsync(CancellationToken stop, TimeSpan? budget) {
        if (budget is not { } limit) return await transport.ConnectAsync(source, stop);

        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(stop);

        var connect = transport.ConnectAsync(source, abandon.Token);

        try {
            return await connect.WaitAsync(limit, time, stop);
        } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !connect.IsCompleted) {
            AbandonedConnect = connect;

            // It may still fault later; observing it keeps that from surfacing as an unobserved exception.
            _ = connect.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await abandon.CancelAsync();

            throw;
        }
    }

    void CancelRun() => _stop.Cancel();

    bool HasLostClaim() {
        if (claims is null) return false;
        if (LostClaim) return true;
        if (claims.Read(source.Key) is not RecorderClaimState.None) return false;

        LostClaim = true;
        context.Log($"The claim on {source.Key} has gone; stopping with no final drain");
        _stop.Cancel();

        return true;
    }

    void Subscribe() {
        if (_subscribed) return;

        _subscribed              =  true;
        transport.StopRequested += OnStopRequestedAsync;
        transport.Reconnected   += OnReconnectedAsync;
    }

    void Unsubscribe() {
        if (!_subscribed) return;

        _subscribed              =  false;
        transport.StopRequested -= OnStopRequestedAsync;
        transport.Reconnected   -= OnReconnectedAsync;
    }

    // A transport raises to a snapshot of its handlers, so one can run after this recorder is disposed. Neither
    // handler may throw, the log included: the raiser awaits them in turn, and a throw would starve the
    // recorders after it.
    Task OnStopRequestedAsync(string key, string reason) {
        if (key != source.Key) return Task.CompletedTask;

        TryLog($"Received StopWatcher signal: {reason}");

        try {
            RequestStop();
        } catch (ObjectDisposedException) {
            // Already finished.
        }

        return Task.CompletedTask;
    }

    async Task OnReconnectedAsync() {
        try {
            if (HasLostClaim()) return;

            var stop           = _stop.Token;
            var serverPosition = await transport.ConnectAsync(source, stop);

            if (serverPosition < State.LinesProcessed) {
                TryLog($"Server behind ({serverPosition} vs {State.LinesProcessed}), rewinding to resend gap");

                var rewound = await GatedApplyReconnectRewindAsync(_cursorRewindGate, State, serverPosition, _cursorGuard, stop);
                PublishAckedLines(State);

                if (!rewound) {
                    // The rewind quarantined the session and moved neither frontier: exit as a detected rewrite does.
                    TryLog("cursor_transcript_rewrite_detected: reconnect frontier beyond local transcript — quarantined, exiting");
                    _stop.Cancel();
                }
            }
        } catch (ObjectDisposedException) {
            // Already finished.
        } catch (Exception ex) {
            TryLog($"Re-register after reconnect failed: {ex.Message}");
        }
    }

    void TryLog(string message) {
        try {
            context.Log(message);
        } catch {
            // A failing log must not reach the transport's other subscribers.
        }
    }

    void FollowMovedTranscript() {
        if (source.Vendor != "claude") return;

        if (File.Exists(TranscriptPath)) {
            _transcriptSeen = true;

            return;
        }

        if ((!_transcriptSeen && State.LinesProcessed == 0) || ClaudeTranscriptMove.Find(TranscriptPath) is not { } moved) return;

        context.Log($"Transcript moved to {moved}; following it");
        TranscriptPath = moved;
    }

    // A rewrite trip has already discarded the unsent batch and quarantined the session.
    void OnCursorRewriteDetected() {
        context.Log("cursor_transcript_rewrite_detected: discarding unsent batch and exiting (session quarantined)");
        _stop.Cancel();
    }

    Task<IReadOnlyList<string>> DrainNewLinesGatedAsync(bool isFinalDrain, CancellationToken ct) {
        context.Housekeeping.RefreshTranscript(TranscriptPath);

        return GatedDrainNewLinesAsync(_cursorRewindGate, State, ct, isFinalDrain, _cursorGuard, OnCursorRewriteDetected);
    }

    /// <summary>Splits a total wait into consecutive chunks of at most <paramref name="maxSlice"/>, so the caller can
    /// beat between them. A non-positive total yields no chunks.</summary>
    public static IReadOnlyList<TimeSpan> HeartbeatSlices(TimeSpan total, TimeSpan maxSlice) {
        var slices    = new List<TimeSpan>();
        var remaining = total;

        while (remaining > TimeSpan.Zero) {
            var chunk = remaining < maxSlice ? remaining : maxSlice;
            slices.Add(chunk);
            remaining -= chunk;
        }

        return slices;
    }

    /// <summary>Vendors whose recorder sends from the first line instead of holding lines back until a real turn:
    /// their start hook, or Antigravity's pre-spawn post, registers the session before anything is read.</summary>
    internal static bool SkipsThresholdBuffering(string vendor) => vendor is "antigravity" or "cursor" or "mistral-vibe" or "grok" or "muse";

    /// <summary>A checkout mutated before the restart, which a resumed recorder's drain never shows its roots.</summary>
    internal static TranscriptReplayConsumer SecondaryRootsReplay(SecondaryRepoRoots roots, string vendor) =>
        new(WindowOnly: false, line => {
            roots.OnLine(vendor, line);
            return roots.IsFull;
        });

    internal static void ObserveCreatedPullRequest(WatchState state, string line) {
        if (CreatedPullRequests.FromClaudeLine(line) is not { } pr) return;
        if (state.LinkedPullRequests.Contains((pr.Owner, pr.RepoName, pr.Number)) || state.PendingCreatedPullRequests.Contains(pr)) return;

        state.PendingCreatedPullRequests.Add(pr);
    }

    /// <summary>A PR created before a restart: nothing records what was linked, and the server takes a PR it
    /// already has as a no-op.</summary>
    internal static TranscriptReplayConsumer CreatedPullRequestsReplay(WatchState state) =>
        new(WindowOnly: false, line => {
            ObserveCreatedPullRequest(state, line);
            return false;
        });

    /// <summary>A harness-native title the server is known to record suppresses LLM titling for good. A transcript
    /// title only a server with harness titles records suppresses it only once the server is known to have them: an
    /// unanswered probe must not cost an older server its only title, and on a newer one the harness title outranks
    /// a generated title anyway. Generation starts at the first assistant reply, or after five events when none has
    /// carried text: a one-shot run ends with fewer than five, and its title has to be under way before it exits.</summary>
    internal static bool ShouldGenerateLlmTitle(WatchState state, string? agentId) =>
        state is { TitleGenerated: false, TitleInFlight: false, TitleAttempts: < 5, ThresholdReached: true, HarnessTitleSeen: false, FirstUserText: not null }
     && (state.FirstAssistantText is not null || state.EventCount >= 5)
     && agentId is null
     && !(state.InlineHarnessTitleSeen && state.ServerRecordsHarnessTitles == true);

    /// <summary>How long shutdown waits for a title still generating, from the time already spent since shutdown began.
    /// Nothing at all once the connection is gone, since the title could not be sent.</summary>
    internal static TimeSpan ShutdownTitleBudget(bool connected, bool stopRequested, TimeSpan sinceShutdown) =>
        connected ? (stopRequested ? ShutdownTitleDeadline : UnhurriedShutdownTitleDeadline) - sinceShutdown : TimeSpan.Zero;

    /// <summary>Waits up to <paramref name="budget"/> for <paramref name="pending"/>, calling <paramref name="beat"/>
    /// before each heartbeat slice of the wait. False when it is still running at the end, for the caller to abandon.</summary>
    internal static async Task<bool> AwaitPendingTitleAsync(Task? pending, TimeSpan budget, TimeProvider time, Action beat, CancellationToken ct = default) {
        if (pending is null || pending.IsCompleted) return true;

        foreach (var slice in HeartbeatSlices(budget, HeartbeatSlice)) {
            beat();

            try {
                await pending.WaitAsync(slice, time, ct);
            } catch (TimeoutException) when (!pending.IsCompleted) {
                continue;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return pending.IsCompleted;
            } catch {
                // the generation's own failure: it is over, which is all shutdown waits for
            }

            return true;
        }

        return pending.IsCompleted;
    }

    internal static void ObserveTitleLine(WatchState state, string vendor, string sessionId, string line) {
        switch (TranscriptTitleLines.Classify(vendor, sessionId, line)) {
            case TranscriptTitleLineKind.RecordedByEveryServer:     state.HarnessTitleSeen       = true; break;
            case TranscriptTitleLineKind.RecordedWithHarnessTitles: state.InlineHarnessTitleSeen = true; break;
        }
    }

    internal static bool RepoPayloadChanged(RepositoryPayload? current, RepositoryPayload? lastSent) {
        if (current is null) {
            return false;
        }

        if (lastSent is null) {
            return true;
        }

        return current.Owner  != lastSent.Owner
         || current.RepoName  != lastSent.RepoName
         || current.Branch    != lastSent.Branch
         || current.PrNumber  != lastSent.PrNumber
         || current.PrUrl     != lastSent.PrUrl
         || current.PrTitle   != lastSent.PrTitle
         || current.PrHeadRef != lastSent.PrHeadRef;
    }

    // Shared by every EvidenceScanner call site: the scanner already resolved and validated completeness.
    public static void ApplyEvidenceRepo(WatchState state, RepositoryPayload repo) {
        state.Repository             = repo;
        state.RepositoryFromEvidence = true;
    }

    // No-op when state.EvidenceScanner is null (every non-Claude or already-in-repo session) or already done.
    internal static async Task ApplyEvidenceScanAsync(WatchState state, string vendor, IReadOnlyList<string> newLines, bool isFinalDrain) {
        if (state.EvidenceScanner is not { Done: false } scanner) return;

        foreach (var line in newLines) {
            if (await scanner.OnLineAsync(vendor, line) is { } repo) {
                ApplyEvidenceRepo(state, repo);

                return;
            }
        }

        if (isFinalDrain && await scanner.PromoteReadFallbackAsync() is { } fallback) {
            ApplyEvidenceRepo(state, fallback);
        }
    }

    static string TruncateForTitle(string text, int maxLength) {
        // Take first line only, then truncate
        var firstLine  = text.AsSpan();
        var newlineIdx = firstLine.IndexOfAny('\r', '\n');

        if (newlineIdx >= 0) {
            firstLine = firstLine[..newlineIdx];
        }

        if (firstLine.Length <= maxLength) {
            return firstLine.ToString().Trim();
        }

        // Find last word boundary before maxLength
        var truncated = firstLine[..maxLength];
        var lastSpace = truncated.LastIndexOf(' ');

        if (lastSpace > maxLength / 2) {
            truncated = truncated[..lastSpace];
        }

        return $"{truncated.ToString().Trim()}...";
    }

    async Task GenerateTitleAsync(WatchState state) {
        var log = context.Log;

        try {
            var result = await context.Housekeeping.GenerateTitleAsync(state.FirstUserText!, state.FirstAssistantText, state.TitleCancellation);

            if (result is null) {
                log($"Title generation attempt {state.TitleAttempts}/5 returned no usable result (CLI failure, refusal-like output, or empty title)");
                state.TitleInFlight = false;

                return;
            }

            log($"Title usage: model={result.Model} input={result.InputTokens} output={result.OutputTokens} cost=${result.CostUsd:F4}");

            await PostTitleAsync(state, result);
        } catch (Exception ex) {
            log($"Title generation failed: {ex.Message}");
            state.TitleInFlight = false;
        }
    }

    async Task SendInitialTitleAsync(string sessionId, string title) {
        try {
            await transport.SendTitleAsync(sessionId, title, CancellationToken.None);
            context.Log($"Initial title sent: {title}");
        } catch (Exception ex) {
            context.Log($"Initial title send failed: {ex.Message}");
        }
    }

    async Task PostTitleAsync(WatchState state, ClaudeCliResult title) {
        try {
            await transport.UpdateTitleAsync(
                source.SessionId, title.Result, title.Model, title.InputTokens, title.OutputTokens, title.CacheReadTokens, title.CacheWriteTokens, CancellationToken.None);
            context.Log($"LLM title generated: {title.Result}");
            state.TitleGenerated = true;
        } catch (Exception ex) {
            context.Log($"LLM title send failed: {ex.Message}");
        }

        state.TitleInFlight = false;
    }

    void StampLines(IReadOnlyList<string> rawLines, IReadOnlyList<int> lineNumbers) {
        for (var i = 0; i < rawLines.Count; i++) stamper.StampFor(lineNumbers[i], rawLines[i]);
    }

    /// <summary>
    /// One poll: reads the lines past the cursor, feeds what tracks them, and sends them. The cursor moves only
    /// past lines the send confirmed, so a failed send re-reads them next poll: event ids are deterministic, so
    /// resending is idempotent. Returns the lines this poll read.
    /// </summary>
    internal async Task<IReadOnlyList<string>> DrainNewLines(
            WatchState          state,
            CancellationToken   ct,
            bool                isFinalDrain            = false,
            CursorRewriteGuard? cursorGuard             = null,
            Action?             onCursorRewriteDetected = null
        ) {
        var sessionId      = source.SessionId;
        var agentId        = source.AgentId;
        var vendor         = source.Vendor;
        var transcriptPath = TranscriptPath;
        var log            = context.Log;

        try {
            if (!File.Exists(transcriptPath)) {
                return [];
            }

            // A quarantined Cursor session delivers nothing more, and a pending hook barrier holds this poll,
            // so a line cannot be normalized ahead of the attachment it depends on.
            if (vendor == "cursor") {
                if (_markers.IsQuarantined(sessionId)) {
                    return [];
                }

                if (_markers.BarrierPending(sessionId, time.GetUtcNow(), CursorMarkers.DefaultBarrierBound)) {
                    return [];
                }
            }

            var cursorGuardOldOffset    = state.CursorByteOffset;
            var priorLineCursorForGuard = state.LinesProcessed;

            // Decided before the read: only the periodic full-prefix re-hash needs the whole file, so any other
            // poll reads a window starting near the guard's prior zone and an idle poll allocates almost nothing.
            var cursorFullPrefixPollNow  = false;
            var cursorBoundedReadFrom    = 0L;
            var cursorGuardPollCountPeek = 0;

            if (vendor == "cursor" && cursorGuard is not null) {
                // The first poll seeds the baseline too: without it a rewrite before poll N has nothing to be
                // caught against, and poll N would bless the rewritten file as the original. The count is only
                // peeked here and committed after the read, or a failed read would skip a due re-hash.
                cursorGuardPollCountPeek = state.CursorGuardPollCount + 1;
                cursorFullPrefixPollNow = cursorGuardPollCountPeek == 1
                    || cursorGuardPollCountPeek % CursorFullPrefixVerifyEveryNPolls == 0;

                if (!cursorFullPrefixPollNow) {
                    cursorBoundedReadFrom = Math.Max(0, cursorGuardOldOffset - cursorGuard.TrailingBytes);
                }
            }

            // Only newline-terminated lines are read. A final line still being written is held back: sending
            // its truncated prefix and advancing past it would drop the completed line for good. Large tool
            // results are slow to flush and get caught mid-write. The next drain re-reads it once complete.
            NewTranscriptLines drainRead;
            await using (var stream = OpenTranscript?.Invoke(transcriptPath) ?? new FileStream(
                    transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                // Cursor captures the raw bytes this read decodes from, so the guard hashes exactly what
                // produced the lines rather than a later read of a file that may have changed.
                drainRead = await TranscriptReader.ReadNewCompleteLinesAsync(
                    stream, state.LinesProcessed,
                    isFinalDrain ? IncompleteFinalLinePolicy.ConsumeIfComplete : IncompleteFinalLinePolicy.Hold,
                    ct, captureRawBytes: vendor == "cursor",
                    rawBytesReadFrom: cursorBoundedReadFrom,
                    newRangeByteOffset: vendor == "cursor" ? cursorGuardOldOffset : null,
                    lineOffsets: state.LineOffsets);
            }

            if (vendor == "cursor" && cursorGuard is not null) {
                state.CursorGuardPollCount = cursorGuardPollCountPeek;
            }

            // Only meaningful for the final drain, which flags the session needs-import rather than drop a
            // truncated tail. Live drains hold routinely.
            if (isFinalDrain) {
                state.FinalDrainHeldIncompleteLine = drainRead.HeldIncompleteFinalLine;
            }

            // The length this read was capped at, not a fresh sample: bytes appended since would be hashed and
            // checkpointed as part of a batch that never saw them, and skipped for good on the next poll.
            var cursorGuardNewLength   = drainRead.SnapshotByteLength;
            var cursorGuardSnapshot    = drainRead.SnapshotBytes;
            var cursorGuardSnapshotAt  = drainRead.SnapshotStartOffset;
            byte[]? cursorGuardVerifiedRange = null;

            if (vendor == "cursor" && cursorGuard is not null && cursorGuardSnapshot is not null) {
                // A shrink trips the guard even though nothing new was read.
                if (!cursorGuard.VerifyNotShrunk(cursorGuardNewLength, cursorGuardOldOffset)) {
                    onCursorRewriteDetected?.Invoke();
                    return [];
                }

                // Independent of growth: an in-place rewrite of checkpointed bytes need not change the length.
                if (!cursorGuard.VerifyPriorZone(cursorGuard.HashPriorZone(cursorGuardSnapshot, cursorGuardSnapshotAt))) {
                    onCursorRewriteDetected?.Invoke();
                    return [];
                }

                if (cursorGuardNewLength > cursorGuardOldOffset) {
                    var rangeLength = (int)(cursorGuardNewLength - cursorGuardOldOffset);
                    var rangeBytes  = cursorGuardSnapshot.AsSpan((int)(cursorGuardOldOffset - cursorGuardSnapshotAt), rangeLength);
                    cursorGuard.RecordNewRangeRead(cursorGuardOldOffset, rangeLength, rangeBytes);
                }

                if (cursorFullPrefixPollNow) {
                    var sample = new CursorAppendOnlyProbe.Sample(
                        cursorGuardSnapshot.Length, CursorAppendOnlyProbe.Sha256Hex(cursorGuardSnapshot));

                    if (!cursorGuard.VerifyFullPrefix(sample, cursorGuardSnapshot)) {
                        onCursorRewriteDetected?.Invoke();
                        return [];
                    }
                }
            }

            // Cut the read itself at a held line: the raw-line consumers below must see what follows
            // it once, on the poll that delivers it.
            var captured = HeldLine.Capture(drainRead.Lines, drainRead.LineNumbers,
                (reason, count) => log($"Capture loss: {count} record(s), {CaptureLossMarker.ReasonName(reason)}"));
            if (captured.Consumed < drainRead.Lines.Count) {
                drainRead = drainRead with {
                    Lines        = drainRead.Lines.GetRange(0, captured.Consumed),
                    LineNumbers  = drainRead.LineNumbers.GetRange(0, captured.Consumed),
                    NextPosition = drainRead.LineNumbers[captured.Consumed]
                };
            }

            var newLineNumbers = drainRead.LineNumbers;
            var newLines       = captured.Lines;
            // The server drops a line numbered at or below one it took, so a shorter file never
            // lowers the cursor; only a reconnect rewind does. kcap import repairs lines written
            // below it.
            var linesRead = Math.Max(drainRead.NextPosition, state.LinesProcessed);

            if (drainRead.NextPosition < state.LinesProcessed) {
                if (!state.ShrinkLogged) {
                    state.ShrinkLogged = true;
                    log($"Transcript is shorter than the line cursor ({drainRead.NextPosition} < {state.LinesProcessed}); keeping the cursor, lines below it are not sent");
                }
            } else {
                state.ShrinkLogged = false;
            }

            state.Commits = state.Commits.Collect();

            // Before the USAGE lines go on, which aren't transcript steps. The created_at anchors USAGE recency,
            // and the pending count suppresses idle-end while a long command runs.
            if (vendor == "antigravity") {
                foreach (var l in newLines) {
                    AntigravityLiveUsage.UpdateAntigravityPendingToolCalls(state, l);
                    if (AntigravityLiveUsage.TryGetAntigravityCreatedAt(l) is { } ca) state.LastAntigravityCreatedAt = ca;
                }
            }

            // Neither is added below the threshold: a line injected there would duplicate on the re-read, or be
            // lost on a failed flush. Both watermarks are committed after a successful send, so a failed batch
            // re-reads the rows.
            var antigravityGenMax = -1L;
            if (vendor == "antigravity" && (agentId is not null || state.ThresholdReached)) {
                antigravityGenMax = AntigravityLiveUsage.AppendAntigravityUsageLines(
                    state, newLines, newLineNumbers, transcriptPath, state.LastAntigravityCreatedAt, context.Housekeeping.ReadAntigravityUsage, log);
            }

            if (vendor == "kiro" && (agentId is not null || state.ThresholdReached)) {
                KiroLiveUsage.AppendKiroUsageBackfillLines(state, newLines, newLineNumbers, transcriptPath, log);
            }

            if (newLines.Count > 0) {
                state.LastActivityAt          = time.GetUtcNow();
                // New activity restarts the idle measure, so discard disconnected time accrued
                // before it (draining only happens while connected, so DisconnectedSince is null).
                state.AccumulatedDisconnected = TimeSpan.Zero;
            }

            // Raw lines: an oversized function_call_output redacts to a placeholder with no call_id, which would
            // strand the call and pin toolInFlight true for good. Never gated on title phase or threshold.
            if (ToolCallTracking.TracksCodexToolCalls(vendor)) {
                foreach (var line in drainRead.Lines) {
                    ToolCallTracking.UpdateCodexPendingToolCalls(state.PendingCodexToolCalls, line);
                }
            }

            if (ToolCallTracking.TracksCopilotToolCalls(vendor, transcriptPath, state.CopilotHostMissing)) {
                foreach (var line in drainRead.Lines) {
                    ToolCallTracking.UpdateCopilotPendingToolCalls(state.PendingCopilotToolCalls, line);
                }
            }

            if (CopilotShutdown.TracksCopilotShutdown(vendor, agentId, transcriptPath)) {
                state.CopilotShutdownSeenAt = CopilotShutdown.CopilotShutdownAfter(state.CopilotShutdownSeenAt, drainRead.Lines, time.GetUtcNow(), state.StartedAt);
            }

            state.ObserveCodexReadings?.Invoke(drainRead.Lines);

            // Capture-loss markers have no tool IDs; local completion tracking needs raw lines.
            if (ToolCallTracking.TracksClaudeToolCalls(vendor, isSessionWatcher: agentId is null)) {
                foreach (var line in drainRead.Lines) {
                    ToolCallTracking.UpdateClaudePendingToolCalls(state.PendingClaudeToolCalls, line);
                }
            }

            // Secondary checkouts, from raw lines and ahead of the threshold buffering below: a
            // mutation in a session's first lines must register before the buffer flushes, and an
            // oversized Write redacts to a placeholder with no path. Only paths are read.
            if (state.SecondaryRoots is { } secondaryRoots) {
                var knownRoots = secondaryRoots.Roots.Count;
                foreach (var line in drainRead.Lines) secondaryRoots.OnLine(vendor, line);
                if (secondaryRoots.Roots.Count > knownRoots) log($"Probing secondary checkouts for a PR: {string.Join(", ", secondaryRoots.Roots)}");
            }

            if (vendor == "claude") {
                foreach (var line in drainRead.Lines) ObserveCreatedPullRequest(state, line);
            }

            // A Codex collab child folds its own turn state, so it can post its stop once done and idle. Raw lines
            // again, in the other direction: a redacted response_item reads as no activity, so a re-engaged child
            // would be reported stopped while working.
            if (vendor == "codex" && agentId is not null) {
                foreach (var line in drainRead.Lines) {
                    state.CodexSubagentTurn.Observe(line);
                }
            }

            // Capture first user text (needed for title generation)
            if (state is { TitleGenerated: false, FirstUserText: null } && agentId is null) {
                // If we resumed from a later position, scan from the beginning of the file
                if (state is { LinesProcessed: > 0, FullFileScanDone: false }) {
                    log("Scanning full file for first user text (resumed from later position)");

                    try {
                        await using var scanStream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var       scanReader = new StreamReader(scanStream);

                        while (await scanReader.ReadLineAsync(ct) is { } scanLine) {
                            if (string.IsNullOrWhiteSpace(scanLine)) {
                                continue;
                            }

                            var userText = TranscriptLineText.TryExtractUserText(scanLine, vendor, _parseErrors);

                            if (userText is null) {
                                continue;
                            }

                            TranscriptLineText.SetFirstUserText(state, userText, log);

                            if (state.FirstUserText is not null) {
                                break;
                            }
                        }

                        state.FullFileScanDone = true;
                    } catch (Exception ex) {
                        log($"Full file scan for user text failed: {ex.Message}");
                    }
                }

                // Also check new lines (normal path)
                if (state.FirstUserText is null && newLines.Count > 0) {
                    foreach (var userText in newLines.Select(line => TranscriptLineText.TryExtractUserText(line, vendor, _parseErrors)).OfType<string>()) {
                        TranscriptLineText.SetFirstUserText(state, userText, log);

                        if (state.FirstUserText is not null) {
                            break;
                        }
                    }
                }

                if (state.FirstUserText is not null) {
                    log($"First user text captured ({state.FirstUserText.Length} chars)");
                }

                // Send truncated user text as the initial title immediately
                // (deferred until threshold is reached for session watchers)
                if (state is { InitialTitleSent: false, FirstUserText: not null, ThresholdReached: true } && agentId is null) {
                    state.InitialTitleSent = true;
                    _                      = SendInitialTitleAsync(sessionId, TruncateForTitle(state.FirstUserText, 80));
                }
            }

            // Count events and capture assistant context for LLM title generation
            if (state is { TitleGenerated: false } && agentId is null && newLines.Count > 0) {
                foreach (var line in newLines) {
                    if (TranscriptLineText.IsEvent(line, vendor)) {
                        state.EventCount++;
                    }

                    if (state.FirstAssistantText is null) {
                        var assistantText = TranscriptLineText.TryExtractAssistantText(line, vendor);

                        if (assistantText is not null) {
                            state.FirstAssistantText = assistantText.Length > 300 ? assistantText[..300] : assistantText;
                            log($"First assistant text captured ({state.FirstAssistantText.Length} chars)");
                        }
                    }

                    if (!state.HarnessTitleSeen) ObserveTitleLine(state, vendor, sessionId, line);
                }
            }

            if (ShouldGenerateLlmTitle(state, agentId)) {
                log($"Triggering LLM title generation (attempt {state.TitleAttempts + 1}/5, events: {state.EventCount})");
                state.TitleInFlight = true;
                state.TitleAttempts++;
                state.PendingTitle  = GenerateTitleAsync(state);
            }

            switch (agentId) {
                // A session nobody took a turn in sends no lines (e.g. one that only ran
                // <local-command-caveat> commands). The cursor stays put while buffering, so every
                // poll re-reads all the unsent lines: the buffer is replaced, never appended to, or
                // it would fill with copies. Subagent watchers never buffer.
                case null when !state.ThresholdReached && newLines.Count > 0: {
                    var previouslyBuffered = state.BufferedLines.Count;
                    state.BufferedLines.Clear();
                    state.BufferedLines.AddRange(newLines);
                    state.BufferedLineNumbers.Clear();
                    state.BufferedLineNumbers.AddRange(newLineNumbers);
                    state.LinesReadAhead = linesRead;

                    if (!TranscriptLineText.ShouldFlushBuffer(state.BufferedLines, vendor, _parseErrors)) {
                        if (state.BufferedLines.Count != previouslyBuffered) {
                            log($"Buffering {state.BufferedLines.Count}/{WatchState.TranscriptThreshold} line(s) until a real turn");
                        }

                        return newLines;
                    }

                    log($"Flushing {state.BufferedLines.Count} buffered line(s)");
                    state.ThresholdReached = true;
                    state.BufferedLines.Clear();
                    state.BufferedLineNumbers.Clear();

                    // Send the initial title now that we're flushing
                    if (state is { InitialTitleSent: false, FirstUserText: not null }) {
                        state.InitialTitleSent = true;
                        _                      = SendInitialTitleAsync(sessionId, TruncateForTitle(state.FirstUserText, 80));
                    }

                    break;
                }
                case null when !state.ThresholdReached:
                    // No new content lines while buffering — track file position
                    state.LinesReadAhead = linesRead;

                    return newLines;
            }

            // Kiro's live context-% has to be on the first send: the server dedupes Kiro events by canonical id.
            if (vendor == "kiro" && newLines.Count > 0) {
                newLines = KiroLiveUsage.EnrichKiroContextUsage(newLines, transcriptPath, _kiroWarnings);
            }

            await ApplyEvidenceScanAsync(state, vendor, newLines, isFinalDrain);

            // Only include repository info when it has changed since last send
            var repoToSend = RepoPayloadChanged(state.Repository, state.LastSentRepository)
                ? state.Repository
                : null;

            // Blank lines advance the line cursor with no send to ack them, so the byte frontier has to advance in
            // the same step. Left behind, the next bounded read would re-read those bytes while numbering lines
            // from the advanced cursor, and the next real line would go out under a wrong number with its ack
            // mapped to the wrong offset.
            void AdvanceCursorBlankByteFrontierInLockstep() {
                if (vendor != "cursor" || cursorGuard is null || cursorGuardSnapshot is null
                 || linesRead <= priorLineCursorForGuard || cursorGuardNewLength <= cursorGuardOldOffset) {
                    return;
                }

                var blankLineCount     = linesRead - priorLineCursorForGuard;
                var rangeLength        = (int)(cursorGuardNewLength - cursorGuardOldOffset);
                var rangeBytes         = cursorGuardSnapshot.AsSpan((int)(cursorGuardOldOffset - cursorGuardSnapshotAt), rangeLength);
                var advancedByteOffset = TranscriptReader.ByteOffsetForAckedLines(rangeBytes, cursorGuardOldOffset, blankLineCount);

                state.CursorByteOffset = advancedByteOffset;

                var trailingLength = (int)Math.Min(cursorGuard.TrailingBytes, advancedByteOffset);
                var trailingHash   = trailingLength <= 0
                    ? CursorAppendOnlyProbe.Sha256Hex(ReadOnlySpan<byte>.Empty)
                    : CursorAppendOnlyProbe.Sha256Hex(
                          cursorGuardSnapshot.AsSpan((int)(advancedByteOffset - trailingLength - cursorGuardSnapshotAt), trailingLength));

                cursorGuard.Checkpoint(advancedByteOffset, trailingHash);
            }

            // A commit filed with no line after it, such as a subagent's under its parent, still goes now.
            if (newLines.Count == 0 && repoToSend is null && state.Commits.Pending is not { Length: > 0 }) {
                AdvanceCursorBlankByteFrontierInLockstep();

                // Nothing to send — safe to advance past blank/whitespace lines
                state.LinesProcessed = linesRead;

                return newLines;
            }

            try {
                // Re-read the same byte range just before sending: a rewrite racing between the read above and
                // this send discards the unsent batch and never advances state.
                if (vendor == "cursor" && cursorGuard is not null && cursorGuardNewLength > cursorGuardOldOffset) {
                    var rangeLength = (int)(cursorGuardNewLength - cursorGuardOldOffset);
                    var rangeBuffer = new byte[rangeLength];

                    await using (var verifyStream = new FileStream(
                            transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                        verifyStream.Position = cursorGuardOldOffset;
                        var rangeRead = await TranscriptReader.ReadFullyAsync(verifyStream, rangeBuffer, ct);

                        if (!cursorGuard.VerifyNewRange(rangeBuffer.AsSpan(0, rangeRead), cursorGuardOldOffset, rangeLength)) {
                            onCursorRewriteDetected?.Invoke();

                            return newLines;
                        }
                    }

                    // The freshest verified copy of the bytes about to go, so the checkpoint below can map the
                    // server's acked line count to a precise offset within them.
                    cursorGuardVerifiedRange = rangeBuffer;
                }

                StampLines(drainRead.Lines, drainRead.LineNumbers);

                // SignalR dispatch binds by arity; the record payload keeps additive fields compatible.
                var batch = new TranscriptBatch {
                    SessionId       = sessionId,
                    AgentId         = agentId,
                    Lines           = newLines.ToArray(),
                    LineNumbers     = newLineNumbers.ToArray(),
                    Repository      = repoToSend,
                    Vendor          = vendor,
                    ObservedCommits = state.Commits.Pending,
                };

                int? cursorAckNextLine = null;

                foreach (var chunk in TranscriptBatchBuffer.Split(batch)) {
                    if (vendor == "cursor") {
                        // A barrier can appear between chunks, after the initial admission check.
                        if (_markers.IsQuarantined(sessionId)
                         || _markers.BarrierPending(sessionId, time.GetUtcNow(), CursorMarkers.DefaultBarrierBound)) {
                            return newLines;
                        }

                        var ack = await transport.SendBatchAsync(chunk, ct);
                        cursorAckNextLine = ack;
                        if (chunk.LineNumbers!.Length > 0 && ack <= chunk.LineNumbers[^1]) break;
                    } else {
                        await transport.SendBatchAsync(chunk, ct);
                    }
                }

                if (newLines.Count > 0) {
                    log(cursorAckNextLine is { } nextLine
                        ? $"Sent {newLines.Count} line(s) via SignalR (acked next-line {nextLine})"
                        : $"Sent {newLines.Count} line(s) via SignalR");
                }

                if (repoToSend is not null) {
                    log("Sent updated repository info via SignalR");
                }

                // Cursor's cursor is the server's acked frontier, not the raw line count sent: a blocked line
                // re-delivers next poll, and an ignored one still advances past.
                state.LinesProcessed = cursorAckNextLine ?? linesRead;
                state.Commits = state.Commits.Delivered(batch.ObservedCommits);
                if (drainRead.Lines.Count > 0) stamper.Acknowledged(state.LinesProcessed - 1);

                if (cursorAckNextLine is not null) {
                    // Only the bytes the ack covers: a partly disposed batch acks fewer lines than were sent, and
                    // an unacked tail checkpointed as delivered would never be sent again. With nothing grown this
                    // poll there is no verified range, and the snapshot length equals the old offset anyway.
                    var ackedLineCount  = cursorAckNextLine.Value - priorLineCursorForGuard;
                    var ackedByteOffset = cursorGuardVerifiedRange is { } verifiedRange
                        ? TranscriptReader.ByteOffsetForAckedLines(verifiedRange, cursorGuardOldOffset, ackedLineCount)
                        : cursorGuardNewLength;

                    state.CursorByteOffset = ackedByteOffset;

                    if (cursorGuard is not null) {
                        // From this poll's snapshot, never a fresh read: a rewrite landing while the ack was in
                        // flight would be blessed as the new baseline instead of caught.
                        var trailingLength = (int)Math.Min(cursorGuard.TrailingBytes, ackedByteOffset);
                        var trailingHash   = trailingLength <= 0 || cursorGuardSnapshot is null
                            ? CursorAppendOnlyProbe.Sha256Hex(ReadOnlySpan<byte>.Empty)
                            : CursorAppendOnlyProbe.Sha256Hex(
                                  cursorGuardSnapshot.AsSpan((int)(ackedByteOffset - trailingLength - cursorGuardSnapshotAt), trailingLength));

                        cursorGuard.Checkpoint(ackedByteOffset, trailingHash);
                    }
                }

                if (antigravityGenMax > state.LastAntigravityGenIdx)
                    state.LastAntigravityGenIdx = antigravityGenMax;

                foreach (var anchor in state.KiroUsagePendingAnchors)
                    state.KiroUsageEmittedAnchors.Add(anchor);

                if (repoToSend is not null) {
                    state.LastSentRepository = repoToSend;
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                if (newLines.Count > 0) {
                    log($"SendTranscriptBatch failed, will retry from line {state.LinesProcessed}: {ex.Message}");
                } else {
                    // A repo-only batch puts no transcript lines at risk, so advance and leave the retry to the next
                    // repo detection. Any lines this poll read were blank, so the byte frontier moves with the cursor.
                    AdvanceCursorBlankByteFrontierInLockstep();

                    state.LinesProcessed    = linesRead;
                    state.LastRepoDetection = time.GetUtcNow();
                    log($"Repo info send failed, will retry in 60s: {ex.Message}");
                }
            }

            return newLines;
        } catch (Exception ex) when (ex is IOException || (isFinalDrain && ex is UnauthorizedAccessException)) {
            log($"Error reading file: {ex.Message}");
        } catch (OperationCanceledException) {
            // Expected during shutdown
        }

        return [];
    }

    /// <summary>Spool only records beyond the acknowledged source frontier.</summary>
    internal async Task<TranscriptSpool.AppendResult?> SpoolUndeliveredTranscriptTailAsync(
            TranscriptSpool      transcriptSpool,
            int                  linesProcessed,
            CommitObservation    commits,
            CancellationToken    ct,
            TimeSpan?            redactionLimit = null,
            TranscriptLineIndex? lineOffsets    = null
        ) {
        var sessionId      = source.SessionId;
        var agentId        = source.AgentId;
        var vendor         = source.Vendor;
        var transcriptPath = TranscriptPath;
        var log            = context.Log;

        if (!File.Exists(transcriptPath)) return null;

        // A quarantined source must not escape through spool replay.
        if (vendor == "cursor" && _markers.IsQuarantined(sessionId)) {
            log($"Cursor session {sessionId} is quarantined; skipping shutdown-tail spool "
              + "(no line-number path may keep feeding a corrupted cursor)");

            return null;
        }

        NewTranscriptLines tail;
        try {
            await using var stream = new FileStream(
                transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            tail = await TranscriptReader.ReadNewCompleteLinesAsync(
                stream, linesProcessed, IncompleteFinalLinePolicy.ConsumeIfComplete, ct, lineOffsets: lineOffsets);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Nothing is left that could say what the final drain missed, and a mark is the only way those lines survive.
            log($"Shutdown-tail spool: failed to read transcript for {sessionId}: {ex.Message}");
            MarkForImport(transcriptSpool, $"shutdown tail: transcript could not be read: {ex.Message}");

            return TranscriptSpool.AppendResult.MarkedNeedsImport;
        }

        commits = commits.Collect();

        if (tail.Lines.Count == 0 && commits.Pending is not { Length: > 0 }) return null;

        var limit    = redactionLimit is { } l && l > MinShutdownTailRedaction ? l : MinShutdownTailRedaction;
        var redacted = TranscriptCapture.EncodeTail(tail.Lines, tail.LineNumbers, new RedactionBudget(context.RedactionClock, limit),
            HeldLine.Redacted,
            (reason, count) => log($"Shutdown capture loss: {count} record(s), {CaptureLossMarker.ReasonName(reason)}"));
        var unredacted = redacted.Consumed < tail.Lines.Count ? tail.LineNumbers[redacted.Consumed] : (int?)null;
        var batch = new TranscriptBatch {
            SessionId = sessionId, AgentId = agentId, Vendor = vendor,
            Lines = [..redacted.Lines], LineNumbers = [..tail.LineNumbers.Take(redacted.Consumed)], ObservedCommits = commits.Pending
        };
        StampLines(tail.Lines.GetRange(0, redacted.Consumed), tail.LineNumbers);
        var result = TranscriptSpool.AppendResult.Appended;
        if (batch.Lines.Length > 0 || commits.Pending is { Length: > 0 }) {
            // A chunk with more to follow leaves its later commits to the next, and an import carries no commits, so it
            // spools only with room left for them on their own.
            var commitsOnly = commits.Pending is { Length: > 0 } pending ? TranscriptSpool.BuildTranscriptSpoolBatch(sessionId, agentId, vendor, [], [], pending) : null;
            var spooled     = false;
            foreach (var chunk in TranscriptBatchBuffer.Split(batch)) {
                result = transcriptSpool.Append(sessionId,
                    TranscriptSpool.BuildTranscriptSpoolBatch(sessionId, agentId, vendor, chunk.Lines, chunk.LineNumbers!, chunk.ObservedCommits, chunk.MoreFollows),
                    chunk.MoreFollows ? commitsOnly : null);
                if (result != TranscriptSpool.AppendResult.Appended) {
                    if (spooled && commitsOnly is not null && transcriptSpool.Append(sessionId, commitsOnly) != TranscriptSpool.AppendResult.Appended)
                        log($"Commits of {sessionId} could not be spooled at shutdown; they are sent again when its watcher resumes");
                    break;
                }
                spooled = true;
            }
        }

        if (result == TranscriptSpool.AppendResult.Appended && unredacted is { } line) {
            log($"Line {line} of {sessionId} still cannot be redacted in time; spooled the {batch.Lines.Length} line(s) before it");
            MarkForImport(transcriptSpool, $"shutdown tail: redaction of line {line} timed out");
            HeldLine.ReleaseBelow(line + 1);
            if (redacted.Consumed > 0) stamper.Acknowledged(line - 1);

            return TranscriptSpool.AppendResult.MarkedNeedsImport;
        }

        switch (result) {
            case TranscriptSpool.AppendResult.Appended:
                HeldLine.ReleaseBelow(tail.NextPosition);
                if (tail.Lines.Count > 0) stamper.Acknowledged(tail.NextPosition - 1);
                log($"Spooled {tail.Lines.Count} undelivered transcript line(s) at shutdown for "
                  + $"{sessionId} — will replay on the next global drain");

                break;
            case TranscriptSpool.AppendResult.MarkedNeedsImport:
                log($"Undelivered transcript tail for {sessionId} could not be spooled (cap exhausted "
                  + "or write failed); session flagged needs-import — recover via `kcap import`");

                break;
        }

        return result;
    }

    void MarkForImport(TranscriptSpool spool, string reason) {
        if (!spool.MarkNeedsImport(source.SessionId, reason)) context.Log($"Could not mark {source.Key} for import: the marker was not written");
    }

    /// <summary>
    /// Moves the cursor back to a server frontier found behind it on reconnect. False, with neither frontier
    /// moved, when <paramref name="serverPosition"/> can't be resolved to an exact byte offset: the session has
    /// been quarantined, and the caller exits as it does for a detected rewrite.
    /// </summary>
    internal Task<bool> ApplyReconnectRewindAsync(WatchState state, int serverPosition, CursorRewriteGuard? cursorGuard, CancellationToken ct) =>
        SeedCursorByteOffsetAsync(state, serverPosition, cursorGuard, ct);

    /// <summary>
    /// Sets the cursor to a server-given line, and for Cursor its true byte offset too, as one step. Both a
    /// reconnect rewind and the initial registration go through here, so a resumed recorder's line and byte
    /// frontiers agree from its first poll. The guard's checkpoint resets, so its zone checks start clean there.
    /// </summary>
    /// <remarks>
    /// <paramref name="lineNumber"/> is the server's acknowledged frontier, which can exceed the local line
    /// count when the transcript was truncated or replaced while nothing watched it. A clamped offset would
    /// seed the guard against the wrong file, so that returns false and quarantines the session, leaving the
    /// offset and checkpoint alone; the caller must not advance the line cursor either.
    /// </remarks>
    internal async Task<bool> SeedCursorByteOffsetAsync(WatchState state, int lineNumber, CursorRewriteGuard? cursorGuard, CancellationToken ct) {
        if (source.Vendor != "cursor" || cursorGuard is null) {
            state.LinesProcessed = lineNumber;
            return true;
        }

        var resolved = await TranscriptReader.ResolveByteOffsetForLineAsync(TranscriptPath, lineNumber, ct);

        if (resolved is null) {
            _markers.Quarantine(
                source.SessionId,
                $"cursor_transcript_rewrite_detected: session {source.SessionId} zone=resume_frontier — "
              + $"server-acknowledged line {lineNumber} exceeds the local transcript's line count");

            return false;
        }

        state.CursorByteOffset = resolved.Value.ByteOffset;
        state.LinesProcessed   = resolved.Value.LineNumber;
        cursorGuard.ResetCheckpoint();

        return true;
    }

    /// <summary>Runs <see cref="ApplyReconnectRewindAsync"/> under <paramref name="gate"/>, the gate
    /// <see cref="GatedDrainNewLinesAsync"/> holds, so a rewind can't land in a drain that already read the
    /// frontiers it is moving. Null for every vendor but Cursor.</summary>
    internal async Task<bool> GatedApplyReconnectRewindAsync(
            SemaphoreSlim? gate, WatchState state, int serverPosition, CursorRewriteGuard? cursorGuard, CancellationToken ct) {
        if (gate is null) {
            return await ApplyReconnectRewindAsync(state, serverPosition, cursorGuard, ct);
        }

        await gate.WaitAsync(ct);
        try {
            return await ApplyReconnectRewindAsync(state, serverPosition, cursorGuard, ct);
        } finally {
            gate.Release();
        }
    }

    /// <summary>Runs <see cref="DrainNewLines"/> under <paramref name="gate"/>, the counterpart of
    /// <see cref="GatedApplyReconnectRewindAsync"/>.</summary>
    internal async Task<IReadOnlyList<string>> GatedDrainNewLinesAsync(
            SemaphoreSlim?      gate,
            WatchState          state,
            CancellationToken   ct,
            bool                isFinalDrain            = false,
            CursorRewriteGuard? cursorGuard             = null,
            Action?             onCursorRewriteDetected = null
        ) {
        if (gate is null) {
            var drained = await DrainNewLines(state, ct, isFinalDrain, cursorGuard, onCursorRewriteDetected);
            PublishAckedLines(state);
            return drained;
        }

        await gate.WaitAsync(ct);
        try {
            var drained = await DrainNewLines(state, ct, isFinalDrain, cursorGuard, onCursorRewriteDetected);
            PublishAckedLines(state);
            return drained;
        } finally {
            gate.Release();
        }
    }
}
