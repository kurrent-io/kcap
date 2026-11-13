using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntime"/> backed by an interactive PTY (Claude, Codex). Wraps an
/// <see cref="IPtyProcess"/> and folds in the CLI-specific input semantics: bracketed-paste input,
/// the <see cref="SpecialKeyMap"/> translation, and the graceful "/exit" stop.
///
/// <paramref name="approvalsDisabled"/> is set by the factory when the launch turned off interactive
/// approval/permission prompts (codex <c>--ask-for-approval never</c> / claude
/// <c>--permission-mode bypassPermissions</c>), so no dialog an Enter could accept can appear. It
/// gates the submit strategy — see <see cref="SubmitAsync"/>. Defaults to <c>false</c> (assume a
/// prompt could be present), the fail-safe choice for interactive/local launches.
///
/// Input writes take the input lane, so a paste and its submit are never interleaved by another
/// writer; the graceful stop only tries for it (see <see cref="RequestGracefulStopAsync"/>).
///
/// <paramref name="readyOnBracketedPaste"/> makes the launch prompt wait for the TUI to enable
/// bracketed paste rather than for any output. The factory sets it on Windows, where ConPTY writes
/// its own preamble before the agent starts; see <see cref="DeliverLaunchPrompt"/>.
///
/// <paramref name="bracketedPasteMeansComposerUp"/> is set when the vendor turns bracketed paste on
/// only while a text input is mounted, so that mode is the readiness signal and no quiet window is
/// waited out after it.
///
/// <paramref name="awaitsSessionStart"/> is set when the vendor fires SessionStart only once its
/// composer is up and that hook reaches this daemon (<see cref="NoteSessionStarted"/>), so the launch
/// prompt waits for it.
///
/// <paramref name="confirmsLaunchPrompt"/> is set when the agent's hooks report its turns to this
/// daemon (<see cref="NoteTurnRelayed"/>), so the launch prompt is not counted as delivered until
/// one does, and any message whose turn is not reported gets its submit Enter once more.
/// </summary>
internal sealed partial class PtyHostedAgentRuntime(
        string vendor, IPtyProcess pty, TimeProvider time, bool approvalsDisabled = false,
        ILogger? logger = null, bool readyOnBracketedPaste = false,
        bool bracketedPasteMeansComposerUp = false,
        bool awaitsSessionStart = false,
        bool confirmsLaunchPrompt = false) : IHostedAgentRuntime {
    /// <summary>
    /// Delays (relative to the previous write) before each carriage return on the spray submit path.
    /// A single CR right after a paste is unreliable: codex's TUI suppresses Enter-as-submit for a
    /// fixed window after ingesting a paste (its <c>PASTE_ENTER_SUPPRESS_WINDOW</c> = 120ms,
    /// timer-driven), treating a CR inside it as a newline. The later CRs here land past the window.
    /// </summary>
    internal static readonly TimeSpan[] SubmitCarriageReturnSchedule = [
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(700),
        TimeSpan.FromMilliseconds(1200),
    ];

    /// <summary>
    /// Past codex's 120ms post-paste Enter-suppression window, so the one CR still submits.
    /// </summary>
    internal static readonly TimeSpan SingleSubmitDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How long the graceful stop waits for the input lane before writing without it. A delivery
    /// parked in an uncancellable <c>write(2)</c> holds the lane until the terminate this stop
    /// precedes, so an unbounded wait here would hold the stop past the reviewer reap's bound.
    /// </summary>
    internal static readonly TimeSpan GracefulStopLaneWait = TimeSpan.FromSeconds(1);

    /// <summary>How long a resubmit waits for the input lane: past a submit spray, and well inside the
    /// round's confirmation window, so a lane freed only after the verdict never gets an Enter the
    /// verdict did not count.</summary>
    internal static readonly TimeSpan ResubmitLaneWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long output must have been silent, once the TUI looks ready, before the launch prompt is
    /// pasted, for a vendor with no signal that its composer is reading input: a TUI still drawing its
    /// banner or startup spinner may not be. Codex's spinner redraws every 100 ms, so the window has to
    /// outlast that gap.
    /// </summary>
    internal static readonly TimeSpan LaunchPromptQuietWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on that wait: a TUI that never settles still gets its prompt.</summary>
    internal static readonly TimeSpan LaunchPromptMaxWait = TimeSpan.FromSeconds(10);

    /// <summary>How long the hooks get to report the turn a submitted launch prompt starts, before the
    /// submit is sent again, and again after that before the prompt is reported unconfirmed. A hook
    /// relays within a second or two of the submit, so this runs out only when the prompt did not go.
    /// Also how long a prompt can be held for SessionStart before it reads as overdue: a follow-up
    /// can wait this long, on the daemon's one sequential SignalR dispatch, before it is refused.</summary>
    internal static readonly TimeSpan LaunchPromptConfirmWait = TimeSpan.FromSeconds(20);

    /// <summary>The same wait for a later message. The agent is up by then and its hooks report a
    /// submitted turn within about two seconds, so this runs out only when the Enter was lost; one
    /// that runs out early costs an Enter in an empty composer.</summary>
    internal static readonly TimeSpan FollowUpConfirmWait = TimeSpan.FromSeconds(5);

    /// <summary>The PTY offers no exit event and the TUI can turn bracketed paste off again, so the
    /// launch prompt's waits are sliced and both re-read between slices.</summary>
    static readonly TimeSpan LaunchPromptPoll = TimeSpan.FromMilliseconds(250);

    readonly TerminalModeTracker _modes = new();
    readonly ILogger _logger = logger ?? NullLogger.Instance;
    readonly SemaphoreSlim           _lane        = new(1, 1);
    readonly CancellationTokenSource _disposal    = new();
    readonly Lock                    _dialogGate  = new();
    readonly Lock                    _arrivalGate = new();
    readonly TaskCompletionSource<bool> _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource    _sessionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource    _overdue        = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long                             _lastOutputAt;
    volatile bool                    _sawOutput;
    volatile bool                    _stopRequested;
    volatile bool                    _stopRequestedDuringDialog;
    bool                             _deliveryHoldsLane;
    TaskCompletionSource?            _dialogGone;
    TaskCompletionSource?            _arrival;
    Task                             _heldSends = Task.CompletedTask;
    TaskCompletionSource             _dialogShown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Unknown until a screen reader reports it: 0 unknown, 1 shown, 2 not shown.
    int                              _promptShown;
    bool                             _unconfirmed;
    Task<bool>?                      _inputReadiness;
    int                              _deliveries;
    int                              _keystrokes;

    /// <summary>The pending launch-prompt delivery, or null when the launch carried no prompt to paste.</summary>
    public Task<LaunchPromptOutcome>? LaunchPromptDelivery { get; private set; }

    /// <summary>Completes once the launch prompt is pasted and submitted: true when startup had settled,
    /// false when it was pasted blind. Never completes for a prompt withdrawn first.</summary>
    internal Task<bool> LaunchPromptSubmitted => _submitted.Task;

    public bool LaunchPromptUnconfirmed {
        get { lock (_arrivalGate) { return _unconfirmed || LaunchPromptDelivery is not null && HeldTooLong; } }
    }

    public Task? LaunchPromptOverdue => awaitsSessionStart ? _overdue.Task : null;

    bool HeldTooLong => _overdue.Task.IsCompleted && !_sessionStarted.Task.IsCompleted;

    public void NoteTurnRelayed() {
        lock (_arrivalGate) {
            _arrival?.TrySetResult();
            _unconfirmed = false;
        }

        // Only a REPL starts a turn, whatever became of the SessionStart notice.
        NoteSessionStarted();
    }

    public void NoteSessionStarted() {
        if (!awaitsSessionStart || _sessionStarted.Task.IsCompleted) return;

        // The hook can fire before the composer mounts, so a screen quiet since before it proves
        // nothing and the quiet window runs from here. Stamped first: the delivery this releases reads it.
        Volatile.Write(ref _lastOutputAt, time.GetTimestamp());
        _sessionStarted.TrySetResult();
    }

    public string  Vendor              => vendor;
    public int     Pid                 => pty.Pid;
    public bool    HasExited           => pty.HasExited;
    public int?    ExitCode            => pty.ExitCode;
    public string? StartIdentity       => pty.StartIdentity;
    public bool    EmitsTerminalOutput => true;

    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        await foreach (var chunk in pty.ReadOutputAsync(ct).ConfigureAwait(false)) {
            // Stamped first: a reader that sees bracketed paste turn on must also see this chunk's time.
            Volatile.Write(ref _lastOutputAt, time.GetTimestamp());
            _modes.Feed(chunk);
            _sawOutput = true;

            yield return chunk;
        }
    }

    bool LooksReadyForInput =>
        readyOnBracketedPaste || bracketedPasteMeansComposerUp ? _modes.IsBracketedPasteEnabled : _sawOutput;

    public void NoteTerminalDialog(bool showing) {
        lock (_dialogGate) {
            if (showing) {
                if (_dialogGone is not null) return;

                _dialogGone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _dialogShown.TrySetResult();
                _dialogShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            } else if (_dialogGone is { } gone) {
                _dialogGone = null;
                gone.TrySetResult();
            }
        }
    }

    public void NotePromptShown(bool shown) => Volatile.Write(ref _promptShown, shown ? 1 : 2);

    Task? DialogGone {
        get { lock (_dialogGate) { return _dialogGone?.Task; } }
    }

    /// <summary>
    /// Makes <paramref name="prompt"/> the agent's first message without putting it on the command
    /// line: pastes it once the TUI looks ready, or <see cref="LaunchPromptMaxWait"/> has passed. Ready
    /// is bracketed paste on where the vendor ties that mode to its composer, and otherwise output
    /// followed by <see cref="LaunchPromptQuietWindow"/> of quiet. A text
    /// follow-up sent meanwhile lands after it; raw and special keys do not wait, so a viewer can
    /// answer a startup dialog themselves. A dialog on screen is not a settled startup and has no
    /// time limit: its default option would take the submit Enter and leave the prompt unsent in the
    /// composer. Neither is a terminal that still rewrites CR, which would turn the submit Enter into a
    /// newline.
    ///
    /// A prompt that awaits SessionStart waits for it ahead of all that, also with no time limit: a
    /// first-run screen fires no hook and is output followed by silence, so it passes every check
    /// here and would take the paste, the Enter and the retry Enter. Past
    /// <see cref="LaunchPromptConfirmWait"/> the hold reads as unconfirmed, which points a viewer at
    /// the terminal, and a follow-up is refused.
    /// </summary>
    /// <remarks>
    /// Under ConPTY output alone is no sign of readiness: it writes its own preamble before the agent
    /// starts, and a paste sent while the agent is still booting reaches its composer but the
    /// submitting CR is lost, leaving the prompt unsent.
    /// </remarks>
    internal void DeliverLaunchPrompt(string prompt) {
        if (LaunchPromptDelivery is not null) throw new InvalidOperationException("The launch prompt is already queued.");

        LaunchPromptDelivery = DeliverLaunchPromptAsync(prompt);
    }

    async Task<LaunchPromptOutcome> DeliverLaunchPromptAsync(string prompt) {
        try {
            bool settled;

            // A dialog can open, or the composer that turned bracketed paste on unmount, while the
            // delivery waits for the lane, so both are checked again once it is held and the wait
            // starts over.
            while (true) {
                settled = await WaitForStartupToSettleAsync(_disposal.Token);
                if (pty.HasExited) return LaunchPromptOutcome.AgentExited;
                if (_stopRequested) return LaunchPromptOutcome.StopRequested;

                await _lane.WaitAsync(_disposal.Token);
                if (DialogGone is null && !(settled && bracketedPasteMeansComposerUp && !_modes.IsBracketedPasteEnabled)) break;

                _lane.Release();
            }

            _deliveryHoldsLane = true;
            Task? arrived = null;
            var keystrokes = Volatile.Read(ref _keystrokes);

            try {
                if (_stopRequested) return LaunchPromptOutcome.StopRequested;

                // Armed before the paste, so only a turn the paste could have started counts.
                if (confirmsLaunchPrompt) {
                    lock (_arrivalGate) {
                        _arrival = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        arrived  = _arrival.Task;
                    }
                }

                await pty.WriteAsync(BracketedPaste.Frame(prompt));
                if (!await SubmitAsync(WaitOutDialogsAsync)) return LaunchPromptOutcome.StopRequested;
            } finally {
                ReleaseDeliveryLane();
            }

            _submitted.TrySetResult(settled);

            if (arrived is null) return settled ? LaunchPromptOutcome.Delivered : LaunchPromptOutcome.DeliveredAfterMaxWait;

            var outcome = await ConfirmArrivalAsync(arrived, () => Volatile.Read(ref _keystrokes) == keystrokes, LaunchPromptConfirmWait);
            if (outcome != LaunchPromptOutcome.NotConfirmed) return outcome;

            lock (_arrivalGate) {
                if (arrived.IsCompleted) return LaunchPromptOutcome.ConfirmedAfterResubmit;
                _unconfirmed = true;
            }

            return outcome;
        } catch (OperationCanceledException) {
            return LaunchPromptOutcome.Cancelled;
        } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
            return LaunchPromptOutcome.WriteFailed;
        }
    }

    /// <summary>
    /// Waits for the hooks to report a turn, which only a submitted prompt starts. When none comes the
    /// submit is sent once more: under ConPTY, or after a paste so large the agent was still taking it
    /// in, the first Enter lands as a newline and leaves the prompt unsent in the composer. That Enter
    /// goes only while no turn has been reported, no dialog shows and <paramref name="mayResubmit"/>
    /// holds: after the prompt arrived it would submit an empty turn, in a dialog it would pick an
    /// option, and behind someone's typing it would submit that too.
    /// </summary>
    async Task<LaunchPromptOutcome> ConfirmArrivalAsync(Task arrived, Func<bool> mayResubmit, TimeSpan wait) {
        if (await ArrivesWithinAsync(arrived, wait)) return LaunchPromptOutcome.Confirmed;
        if (Withdrawn() is { } early) return early;

        // A write parked in the lane would otherwise hold the verdict as well as the Enter.
        using (var expiry = new CancellationTokenSource(ResubmitLaneWait, time))
        using (var bound = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token, expiry.Token)) {
            try {
                await _lane.WaitAsync(bound.Token);
            } catch (OperationCanceledException) when (!_disposal.IsCancellationRequested) {
                return arrived.IsCompleted ? LaunchPromptOutcome.Confirmed : LaunchPromptOutcome.NotConfirmed;
            }
        }
        _deliveryHoldsLane = true;

        try {
            if (!await WaitOutDialogsAsync()) return LaunchPromptOutcome.StopRequested;
            if (arrived.IsCompleted) return LaunchPromptOutcome.Confirmed;
            if (!mayResubmit()) return LaunchPromptOutcome.NotConfirmed;
            if (!await WriteSubmitCarriageReturnAsync())
                return pty.HasExited ? LaunchPromptOutcome.AgentExited : LaunchPromptOutcome.WriteFailed;
        } finally {
            ReleaseDeliveryLane();
        }

        if (await ArrivesWithinAsync(arrived, wait)) return LaunchPromptOutcome.ConfirmedAfterResubmit;
        if (Withdrawn() is { } late) return late;

        return arrived.IsCompleted ? LaunchPromptOutcome.ConfirmedAfterResubmit : LaunchPromptOutcome.NotConfirmed;
    }

    LaunchPromptOutcome? Withdrawn() =>
        pty.HasExited ? LaunchPromptOutcome.AgentExited : _stopRequested ? LaunchPromptOutcome.StopRequested : null;

    /// <summary>True once a turn is reported; false when <paramref name="wait"/> passes
    /// first or the agent exits or is asked to stop. The wait starts over while a dialog shows: the
    /// prompt cannot start a turn until it is answered.</summary>
    async Task<bool> ArrivesWithinAsync(Task arrived, TimeSpan wait) {
        var started = time.GetTimestamp();

        while (!arrived.IsCompleted && !pty.HasExited && !_stopRequested) {
            if (DialogGone is not null) started = time.GetTimestamp();

            var remaining = wait - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return false;

            try {
                await arrived.WaitAsync(remaining < LaunchPromptPoll ? remaining : LaunchPromptPoll, time, _disposal.Token);
            } catch (TimeoutException) { }
        }

        return arrived.IsCompleted;
    }

    void ReleaseDeliveryLane() {
        if (!_deliveryHoldsLane) return;

        _deliveryHoldsLane = false;
        try { _lane.Release(); } catch (ObjectDisposedException) { }
    }

    /// <summary>A follow-up waits for the launch prompt so it lands after it. The delivery reports
    /// its own fate; the follow-up only needs it to be over. A prompt held too long refuses the
    /// follow-up instead: that hold has no time limit, and a server dispatch awaits this on the
    /// daemon's one sequential SignalR dispatch, ahead of every other agent's stop and launch.</summary>
    async Task WaitForLaunchPromptAsync() {
        if (((Task?)LaunchPromptDelivery ?? _inputReadiness) is not { } delivery) return;

        await Task.WhenAny(delivery, _overdue.Task);
        if (HeldTooLong && !delivery.IsCompleted)
            throw new IOException("the agent is still starting: finish what its terminal is asking, then send this again");

        try { await delivery; } catch (Exception) { }

        if (delivery == _inputReadiness && Interlocked.Exchange(ref _inputReadiness, null) is { } readiness && !await readiness)
            LogInputSentUnsettled(_logger, vendor, LaunchPromptMaxWait.TotalSeconds);
    }

    /// <summary>Holds the first text input of an agent spawned with no launch prompt, such as one resumed
    /// idle, until its terminal is ready for a paste on the launch prompt's terms. Input sent once
    /// <see cref="LaunchPromptMaxWait"/> has run out is pasted anyway, and logged as unproven.</summary>
    internal void HoldInputUntilReady() {
        if (LaunchPromptDelivery is not null) throw new InvalidOperationException("The launch prompt already holds input.");

        _inputReadiness ??= WaitForInputReadinessAsync();
    }

    async Task<bool> WaitForInputReadinessAsync() {
        try {
            return await WaitForStartupToSettleAsync(_disposal.Token);
        } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) {
            return true;
        }
    }

    /// <summary>True once the TUI is ready for a paste, false when the wait expired first.
    /// Returns early, true, when the agent has exited or been asked to stop; the caller checks those.</summary>
    async Task<bool> WaitForStartupToSettleAsync(CancellationToken ct) {
        await WaitForSessionStartAsync(ct);

        var started = time.GetTimestamp();

        var sawCooked  = false;
        var uncookedAt = 0L;

        while (!pty.HasExited && !_stopRequested) {
            if (DialogGone is not null) {
                await WaitWhileDialogShowsAsync(ct);
                started = time.GetTimestamp();

                continue;
            }

            // Before the TUI turns raw input on, the line discipline rewrites the submit CR as LF,
            // which the composer takes as a newline: the prompt sits there unsent. The switch is no
            // sign the composer is drawn, so the quiet window runs again from it.
            if (pty.IsInputCooked) {
                await Task.Delay(LaunchPromptPoll, time, ct);
                started = time.GetTimestamp();
                sawCooked = true;

                continue;
            }

            if (sawCooked) {
                uncookedAt = time.GetTimestamp();
                sawCooked  = false;
            }

            var remaining = LaunchPromptMaxWait - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return false;

            if (!LooksReadyForInput) {
                await Task.Delay(remaining < LaunchPromptPoll ? remaining : LaunchPromptPoll, time, ct);

                continue;
            }

            if (bracketedPasteMeansComposerUp) return true;

            var quietFor = time.GetElapsedTime(Math.Max(Volatile.Read(ref _lastOutputAt), uncookedAt));
            if (quietFor >= LaunchPromptQuietWindow) return true;

            await Task.Delay(LaunchPromptQuietWindow - quietFor, time, ct);
        }

        return true;
    }

    /// <summary>Returns once SessionStart has been reported, or the agent has exited or been asked to stop.</summary>
    async Task WaitForSessionStartAsync(CancellationToken ct) {
        if (!awaitsSessionStart) return;

        var started = time.GetTimestamp();

        while (!_sessionStarted.Task.IsCompleted && !pty.HasExited && !_stopRequested) {
            try {
                await _sessionStarted.Task.WaitAsync(LaunchPromptPoll, time, ct);
            } catch (TimeoutException) { }

            if (time.GetElapsedTime(started) >= LaunchPromptConfirmWait) _overdue.TrySetResult();
        }
    }

    /// <summary>Called by a delivery holding the input lane, before each submit Enter. A dialog
    /// that came up after the paste would take the Enter, and answering it takes the lane, so each one is
    /// waited out with the lane released. Returns holding the lane; false when a stop was asked for.</summary>
    async Task<bool> WaitOutDialogsAsync() {
        await WaitOutDialogsReportingAsync();

        return !_stopRequested;
    }

    /// <summary><see cref="WaitOutDialogsAsync"/>, saying whether any dialog was waited out.</summary>
    async Task<bool> WaitOutDialogsReportingAsync() {
        var waited = false;

        while (DialogGone is not null && !pty.HasExited && !_stopRequested) {
            waited = true;
            _lane.Release();
            _deliveryHoldsLane = false;
            await WaitOutDialogAsync();
            await _lane.WaitAsync(_disposal.Token);
            _deliveryHoldsLane = true;
        }

        return waited;
    }

    /// <summary>Returns once no dialog is on screen and the redraw after it has settled, or the agent
    /// has exited or been asked to stop. A paste or Enter that lands in that redraw is lost, and answering
    /// the dialog was a key press, so nothing would resubmit it. Call it without the lane: the keys that
    /// answer a dialog shown again meanwhile need it.</summary>
    async Task WaitOutDialogAsync() {
        // A dialog redrawn as it is answered can read as gone for a chunk, so the wait ends only once the
        // dialog has stayed gone through the settle.
        while (DialogGone is not null && !pty.HasExited && !_stopRequested) {
            await WaitWhileDialogShowsAsync(_disposal.Token);
            await WaitForRedrawToSettleAsync();
        }
    }

    /// <summary>Returns once no dialog is on screen, or the agent has exited or been asked to stop.</summary>
    async Task WaitWhileDialogShowsAsync(CancellationToken ct) {
        while (DialogGone is { } gone && !pty.HasExited && !_stopRequested) {
            try {
                await gone.WaitAsync(LaunchPromptPoll, time, ct);
            } catch (TimeoutException) { }
        }
    }

    /// <summary>
    /// A dispatched message that a dialog holds back, or that follows one held, is taken at once and
    /// written in order once the dialog closes: the daemon handles server messages one at a time, and
    /// awaiting this write would hold back the keys that answer the dialog. One that meets a dialog
    /// opening mid-write is let go the same way.
    /// </summary>
    public async Task<Task> SendDispatchedInputAsync(string text, Guid dispatchId) {
        Task dialogShown;
        lock (_dialogGate) dialogShown = _dialogShown.Task;

        var (write, held) = Enqueue(text);

        if (held) return write;

        // A dialog can also open between the paste and its Enter, which then waits for it.
        if (await Task.WhenAny(write, dialogShown) != write) return write;

        await write;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Delivers <paramref name="text"/> as a bracketed paste (ESC[200~ … ESC[201~) so the TUI treats
    /// it as one block, then submits it (see <see cref="SubmitAsync"/>), after any message already
    /// waiting on a dialog.
    /// </summary>
    /// <exception cref="IOException">The agent was stopping or had exited before the message could be sent.</exception>
    public Task SendUserInputAsync(string text) => Enqueue(text).Write;

    // Every message joins one chain, so one sent while another waits on a dialog lands after it, not in its redraw.
    (Task Write, bool Held) Enqueue(string text) {
        lock (_dialogGate) {
            var held = _dialogGone is not null || !_heldSends.IsCompleted;
            _heldSends = WriteAfterAsync(_heldSends, text);

            return (_heldSends, held);
        }
    }

    async Task WriteAfterAsync(Task previous, string text) {
        try {
            await previous;
        } catch (Exception) {
            // The message before reports its own loss; this one is still written.
        }

        await PasteAndSubmitAsync(text);
    }

    /// <summary>Waits for the composer, the agent's prompt box where its screen is read, and a quiet
    /// spell, as the launch prompt does. Returns at once if a dialog shows again.</summary>
    async Task WaitForRedrawToSettleAsync() {
        var started = time.GetTimestamp();

        while (DialogGone is null && !pty.HasExited && !_stopRequested && time.GetElapsedTime(started) < LaunchPromptMaxWait) {
            var quietFor = time.GetElapsedTime(Volatile.Read(ref _lastOutputAt));
            // Quiet alone is no proof: Claude can go silent between erasing a dialog and drawing its prompt again.
            // A turn the dialog interrupted keeps drawing once it is answered, so a prompt box that has stood
            // through the quiet window is enough.
            var prompt = Volatile.Read(ref _promptShown);
            if (LooksReadyForInput && prompt != 2
             && (quietFor >= LaunchPromptQuietWindow || prompt == 1 && time.GetElapsedTime(started) >= LaunchPromptQuietWindow)) return;

            await Task.Delay(quietFor < LaunchPromptQuietWindow ? LaunchPromptQuietWindow - quietFor : LaunchPromptPoll, time, _disposal.Token);
        }
    }

    /// <summary>
    /// Text typed into an open dialog would answer it, and the server may have refused this sender that
    /// answer, so the paste and each submit Enter wait for any dialog to close, with the lane released so
    /// the dialog can be answered.
    /// </summary>
    async Task PasteAndSubmitAsync(string text) {
        await WaitForLaunchPromptAsync();

        // Once a stop is asked for, a paste would land behind its "/exit", whatever dialog came and
        // went while this waited for the lane.
        while (true) {
            await WaitOutDialogAsync();
            await _lane.WaitAsync(_disposal.Token);

            if (Ending) {
                _lane.Release();
                throw AgentEnding();
            }

            if (DialogGone is null) break;

            _lane.Release();
        }

        _deliveryHoldsLane = true;
        var delivery   = Interlocked.Increment(ref _deliveries);
        var keystrokes = Volatile.Read(ref _keystrokes);
        Task? arrived  = null;

        try {
            // A message still awaiting its turn shares this one's: the turn either starts reads as
            // both arriving, so neither reports a miss the other's turn answered.
            if (confirmsLaunchPrompt) {
                lock (_arrivalGate) {
                    if (_arrival is not { Task.IsCompleted: false })
                        _arrival = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    arrived = _arrival.Task;
                }
            }

            await pty.WriteAsync(BracketedPaste.Frame(text));
            // Unlike the launch prompt, a stop alone does not withhold the Enter: the stop's "/exit"
            // would land in the composer behind the unsent paste. A stop asked for while a dialog this
            // waited on was open does, even once that dialog has closed: the stop wrote meanwhile.
            var submitted = await SubmitAsync(async () => {
                var waited = await WaitOutDialogsReportingAsync();
                return DialogGone is null && !pty.HasExited && !(waited && _stopRequestedDuringDialog);
            });
            if (!submitted) throw AgentEnding();
        } finally {
            ReleaseDeliveryLane();
        }

        // The sender has its answer once the Enter is written; the receipt can take a whole turn.
        if (arrived is not null) FollowUpConfirmation = ConfirmFollowUpAsync(arrived, delivery, keystrokes);
    }

    /// <summary>The latest follow-up's confirmation, for tests; null until a confirming runtime sends one.</summary>
    internal Task<LaunchPromptOutcome>? FollowUpConfirmation { get; private set; }

    /// <summary>Any reported turn boundary counts as the receipt, so a message sent mid-turn is taken
    /// as arrived when that turn ends and is never resubmitted. The retry is for an idle agent, where
    /// the next boundary is the turn the message starts.</summary>
    async Task<LaunchPromptOutcome> ConfirmFollowUpAsync(Task arrived, int delivery, int keystrokes) {
        try {
            var outcome = await ConfirmArrivalAsync(arrived, () =>
                Volatile.Read(ref _deliveries) == delivery && Volatile.Read(ref _keystrokes) == keystrokes, FollowUpConfirmWait);
            if (outcome == LaunchPromptOutcome.NotConfirmed) LogFollowUpNotConfirmed(_logger, vendor);
            return outcome;
        } catch (OperationCanceledException) {
            return LaunchPromptOutcome.Cancelled;
        } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
            return LaunchPromptOutcome.WriteFailed;
        }
    }

    bool Ending => _stopRequested || pty.HasExited;

    static IOException AgentEnding() => new("The agent is stopping or has exited, so the message was not sent.");

    public async Task SendSpecialKeyAsync(string key) {
        var isWheel = TerminalScrollKey.TryDecode(key, out var bytes);
        if (!isWheel) bytes = SpecialKeyMap.ToBytes(key);
        if (bytes.Length == 0) return;

        await _lane.WaitAsync();

        try {
            if (isWheel && (!_modes.IsMouseReporting || pty.HasExited)) {
                LogWheelDropped(_logger);
                return;
            }
            if (!isWheel) Interlocked.Increment(ref _keystrokes);
            await pty.WriteAsync(bytes);
        } finally {
            _lane.Release();
        }
    }

    public async Task SendRawInputAsync(byte[] data) {
        await _lane.WaitAsync();

        try {
            if (data.Length > 0 && !TerminalReply.IsOnlyReplies(data)) Interlocked.Increment(ref _keystrokes);
            await pty.WriteAsync(data);
        } finally {
            _lane.Release();
        }
    }

    /// <summary>Withheld while a dialog shows, since its default option would take the Enter, and once
    /// <paramref name="isCurrent"/> is false: a newer delivery may have released the lane this waited on,
    /// and the Enter would submit that round instead.</summary>
    public async Task<bool> ResubmitAsync(Func<bool> isCurrent) {
        try {
            if (!await _lane.WaitAsync(ResubmitLaneWait, _disposal.Token)) return false;
        } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) {
            return false;
        }

        try {
            if (_stopRequested || DialogGone is not null || !isCurrent()) return false;

            return await WriteSubmitCarriageReturnAsync();
        } finally {
            try { _lane.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public void Resize(ushort cols, ushort rows) => pty.Resize(cols, rows);

    /// <summary>
    /// Writes "/exit" and submits it. Takes the input lane when it can, but gives up after
    /// <see cref="GracefulStopLaneWait"/> and writes anyway: on a real PTY both writers share the
    /// master fd regardless, and the lane must never be what keeps the stop from being asked for.
    /// </summary>
    public async Task RequestGracefulStopAsync() {
        if (DialogGone is not null) _stopRequestedDuringDialog = true;
        _stopRequested = true;
        var holdsLane  = await _lane.WaitAsync(GracefulStopLaneWait, CancellationToken.None);

        try {
            await pty.WriteAsync("/exit");
            await SubmitAsync();
        } finally {
            if (holdsLane) _lane.Release();
        }
    }

    /// <summary>
    /// Submits the composer. When <c>approvalsDisabled</c> (no dialog an Enter could accept),
    /// sprays carriage returns on <see cref="SubmitCarriageReturnSchedule"/> so at least one lands
    /// past codex's post-paste Enter-suppression window; the extra CRs are then empty-composer
    /// no-ops. Otherwise sends a single CR — in an interactive session a stray Enter must not
    /// answer a live approval prompt.
    /// </summary>
    /// <param name="beforeCarriageReturn">Runs before each CR; false withholds it and the rest.</param>
    /// <returns>False when <paramref name="beforeCarriageReturn"/> withheld a CR.</returns>
    async Task<bool> SubmitAsync(Func<Task<bool>>? beforeCarriageReturn = null) {
        if (approvalsDisabled) {
            foreach (var delay in SubmitCarriageReturnSchedule) {
                if (pty.HasExited) return true;
                await Task.Delay(delay, time);
                if (beforeCarriageReturn is not null && !await beforeCarriageReturn()) return false;
                if (!await WriteSubmitCarriageReturnAsync()) return true; // stop once the reviewer is gone
            }

            return true;
        }

        await Task.Delay(SingleSubmitDelay, time);
        if (beforeCarriageReturn is not null && !await beforeCarriageReturn()) return false;
        await WriteSubmitCarriageReturnAsync();

        return true;
    }

    /// <summary>
    /// Writes one submit CR. Returns <c>false</c> if the process has already exited or its input pipe
    /// closed mid-write — a benign post-exit write (<see cref="Pty.IPtyProcess.WriteAsync(string)"/>
    /// is unguarded and throws on a closed pipe), which must not propagate as a graceful-exit failure.
    /// </summary>
    async Task<bool> WriteSubmitCarriageReturnAsync() {
        if (pty.HasExited) return false;

        try {
            await pty.WriteAsync("\r");

            return true;
        } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Vendor} agent's first message was pasted without its terminal settling within {Seconds:F0}s, so it may not have been submitted")]
    static partial void LogInputSentUnsettled(ILogger logger, string vendor, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Vendor} agent reported no turn for a message after its submit was sent twice")]
    static partial void LogFollowUpNotConfirmed(ILogger logger, string vendor);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped wheel input while PTY mouse reporting is inactive or the process has exited")]
    static partial void LogWheelDropped(ILogger logger);

    public Task WaitForExitAsync(TimeSpan? timeout = null) => pty.WaitForExitAsync(timeout);
    public Task TerminateAsync(TimeSpan?   timeout = null) => pty.TerminateAsync(timeout);

    public async ValueTask DisposeAsync() {
        await _disposal.CancelAsync();
        await pty.DisposeAsync();
        _lane.Dispose();
        _disposal.Dispose();
    }
}
