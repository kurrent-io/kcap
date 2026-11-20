using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Vendor-agnostic hosted-agent process. <see cref="AgentOrchestrator"/> owns the vendor-neutral
/// lifecycle and only ever touches this interface; the runtime owns the vendor's process and I/O.
/// </summary>
internal interface IHostedAgentRuntime : IAsyncDisposable {
    /// <summary>Vendor token this runtime hosts ("claude", "codex", "cursor").</summary>
    string Vendor { get; }

    /// <summary>The transport label reported on registration, which the server validates its launch
    /// decision against. Only the Codex app-server runtime reports something other than "pty".</summary>
    string RuntimeTransport => "pty";

    /// <summary>What the vendor's resume takes when it is not the session id; recorded as the vendor
    /// session in the resume ledger. Null when the session id is enough.</summary>
    string? VendorResumeToken => null;

    /// <summary>OS process id of the hosted agent (for logging).</summary>
    int Pid { get; }

    /// <summary>True once the hosted process has exited.</summary>
    bool HasExited { get; }

    /// <summary>Exit code once <see cref="HasExited"/>; null while running or if unknown.</summary>
    int? ExitCode { get; }

    /// <summary>The start identity captured inside the spawn call, before the child can be reaped. Null
    /// when this runtime does not capture one (Windows, ACP), and the caller falls back to
    /// <c>ProcessStartToken.ForPid</c>. On Unix never null: <c>""</c> means capture failed, and the caller
    /// must not re-capture, since a later capture could name a recycled pid.</summary>
    string? StartIdentity => null;

    /// <summary>
    /// Whether <see cref="ReadOutputAsync"/> yields terminal bytes. A PTY runtime flips Running on the
    /// first chunk and reads the stream ending as a possible startup failure; a runtime without terminal
    /// output needs the other strategy or it would never show Running and every exit would look like a
    /// startup failure.
    /// </summary>
    bool EmitsTerminalOutput { get; }

    /// <summary>Whether a dialog read off the terminal is holding the agent's input. Called on the
    /// output read loop for the chunk it was read from, before the next chunk is read.</summary>
    void NoteTerminalDialog(bool showing) { }

    /// <summary>Whether the agent's own prompt box is on the terminal, for a runtime whose screen is
    /// read. Called on the output read loop like <see cref="NoteTerminalDialog"/>.</summary>
    void NotePromptShown(bool shown) { }

    /// <summary>A hook relayed a turn starting or ending for this agent, which only a submitted prompt
    /// causes.</summary>
    void NoteTurnRelayed() { }

    /// <summary>The agent's SessionStart hook ran, so a launch prompt held for it can go.</summary>
    void NoteSessionStarted() { }

    /// <summary>Sends one more submit Enter for input already in a terminal composer. False when nothing
    /// was written: no composer, a dialog that would take the Enter, a stop, the agent gone, or
    /// <paramref name="isCurrent"/> false once the input lane is held, because a newer input owns the
    /// composer by then.</summary>
    Task<bool> ResubmitAsync(Func<bool> isCurrent) => Task.FromResult(false);

    /// <summary>The launch prompt pasted after spawn, or null when nothing is pasted.</summary>
    Task<LaunchPromptOutcome>? LaunchPromptDelivery => null;

    /// <summary>The launch prompt was not seen to arrive, or is still held for SessionStart past the
    /// time a start should take, and no turn has been relayed since.</summary>
    bool LaunchPromptUnconfirmed => false;

    /// <summary>Completes when a launch prompt held for SessionStart has waited past the time a start
    /// should take. Null for a prompt that is not held.</summary>
    Task? LaunchPromptOverdue => null;

    /// <summary>Terminal byte stream for the orchestrator's read loop; empty for a runtime whose stdout
    /// is protocol traffic.</summary>
    IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default);

    /// <summary>
    /// Hosted-UI text input (server <c>SendInput</c>). PTY runtimes perform the CLI-specific
    /// text-then-Enter split write; the ACP runtime sends a <c>session/prompt</c>. Fails with
    /// <see cref="InputNotAdmittedException"/> when the runtime will not queue or write the text (a
    /// full pending-turn queue, a terminal runtime): thrown synchronously by this method, carried by
    /// the task from <see cref="SendUserInputAndWaitForWriteAsync"/>.
    /// </summary>
    Task SendUserInputAsync(string text);

    /// <summary>Queue input and complete only after the runtime has written the prompt to its
    /// protocol transport. Borrowed snapshot rounds use this acknowledgement to close the race
    /// between prompt delivery and a subsequent refresh. PTY runtimes use the normal send. Fails with
    /// <see cref="InputNotAdmittedException"/> when the runtime will not queue or write the text (a
    /// full pending-turn queue, a terminal runtime): thrown synchronously by
    /// <see cref="SendUserInputAsync"/>, carried by the task from this method.</summary>
    Task SendUserInputAndWaitForWriteAsync(string text) => SendUserInputAsync(text);

    /// <summary>Input a server dispatch delivered. Completes once the runtime has taken the input, failing
    /// with <see cref="InputNotAdmittedException"/> when it will not; the task it yields completes once the
    /// input is written, and faults if it never will be, which for input queued behind an active turn can
    /// be long after it was taken. A runtime that records the user message it sends stamps the dispatch on
    /// it, so a viewer of its journal can ask the server who sent the message.</summary>
    async Task<Task> SendDispatchedInputAsync(string text, Guid dispatchId) {
        await SendUserInputAsync(text).ConfigureAwait(false);

        return Task.CompletedTask;
    }

    /// <summary>Starts compacting the agent's context, or says why it will not. Synchronous, so a caller can hold its
    /// own guard across the decision and the start. A runtime that records the user messages it sends records the
    /// request as one, stamped with <paramref name="dispatchId"/>.</summary>
    CompactionStart StartCompaction(Guid dispatchId) => CompactionStart.Unsupported;

    /// <summary>Wait until any prior structured prompt turn has received its terminal response.
    /// PTY runtimes have no structured turn boundary and use the completed default.</summary>
    Task WaitForTurnIdleAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// True when this runtime returns from its start WITHOUT dispatching the first turn — it holds the
    /// initial prompt and seals input — and the orchestrator MUST durably source-claim the session and
    /// then call <see cref="BeginFirstTurnAsync"/>. Only the envelope-sourced hosted-Codex runtime does
    /// this; every other runtime keeps the single-phase launch (default false), so the orchestrator
    /// skips the extra handshake for them.
    /// </summary>
    bool RequiresSourceClaimBeforeFirstTurn => false;

    /// <summary>
    /// Dispatch the held initial prompt as the first turn and unseal any queued input, completing when
    /// the first turn's transport response has landed. Called once by the orchestrator after the source
    /// claim acks, only when <see cref="RequiresSourceClaimBeforeFirstTurn"/> is true. No-op default for
    /// every single-phase runtime.
    /// </summary>
    Task BeginFirstTurnAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Hosted-UI special key (server <c>SendSpecialKey</c>). PTY runtimes translate via
    /// <see cref="SpecialKeyMap"/> and write the bytes; the ACP runtime maps or ignores.
    /// </summary>
    Task SendSpecialKeyAsync(string key);

    /// <summary>
    /// Raw byte input from a local-attached terminal (`kcap agent attach` Stdin frames). PTY runtimes
    /// write the bytes verbatim. Local attach is a PTY-only surface, so the ACP runtime throws
    /// <see cref="NotSupportedException"/>; the caller guards on <see cref="Vendor"/>.
    /// </summary>
    Task SendRawInputAsync(byte[] data);

    /// <summary>Viewer resize; a no-op without a terminal.</summary>
    void Resize(ushort cols, ushort rows);

    /// <summary>Set by the orchestrator after the runtime starts. The runtime invokes it whenever it
    /// learns the harness's slash commands — an ACP <c>available_commands_update</c> or
    /// <c>session/new</c> result, a Codex <c>skills/list</c>. A snapshot captured during start is
    /// delivered the moment the callback attaches (see <see cref="HostedAgentCommandsRelay"/>), so
    /// nothing races the attach. PTY runtimes never raise it — the orchestrator probes Claude
    /// separately.</summary>
    Action<IReadOnlyList<HostedAgentCommand>>? OnCommandsAvailable {
        get => null;
        set { }
    }

    /// <summary>
    /// Request a graceful stop <b>before</b> <see cref="TerminateAsync"/>. PTY runtimes send the
    /// CLI's "/exit" so it can fire its own session-end hook; the ACP runtime sends
    /// <c>session/cancel</c> and closes the agent's stdin. Best-effort — the orchestrator falls
    /// through to terminate.
    /// </summary>
    Task RequestGracefulStopAsync();

    /// <summary>Wait up to <paramref name="timeout"/> for the process to exit (returns silently on timeout).</summary>
    Task WaitForExitAsync(TimeSpan? timeout = null);

    /// <summary>Terminate the process (SIGTERM then SIGKILL) within <paramref name="timeout"/>.</summary>
    Task TerminateAsync(TimeSpan? timeout = null);
}
