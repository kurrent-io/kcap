using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Harness.Antigravity;

/// <summary>
/// One <c>agy -p</c> turn's process, including its NDJSON line stream: the exec-per-turn counterpart of
/// <see cref="IAcpProcess"/>. The runtime's spawner returns a fresh instance per turn.
/// <para>A real implementation must honour three things:</para>
/// <para>0. <see cref="IAsyncDisposable.DisposeAsync"/> may kill a running child, so it is not exit
/// evidence: read <see cref="HasExited"/> before disposing.</para>
/// <para>1. <see cref="IAsyncDisposable.DisposeAsync"/> is idempotent; the turn's <c>finally</c> and the
/// runtime's dispose can both reach it.</para>
/// <para>2. <see cref="TerminateAsync"/> is safe after disposal, since a runtime terminate can race the
/// turn worker's <c>finally</c>.</para>
/// </summary>
internal interface IAgyTurnProcess : IAsyncDisposable {
    /// <summary>OS process id of this turn's <c>agy -p</c> child.</summary>
    int Pid { get; }

    /// <summary>True once this turn's process has exited.</summary>
    bool HasExited { get; }

    /// <summary>OS exit code once <see cref="HasExited"/>; null while running or if unknown. This is
    /// the raw OS code for THIS ONE TURN — never confused with
    /// <c>AntigravityHostedAgentRuntime.ExitCode</c>, which is derived from the agy <c>result</c>
    /// event's <c>status</c> field across the runtime's whole logical lifetime, not any single
    /// turn's OS exit code (upstream bug: a clean run can carry an empty <c>response</c>, so
    /// <c>status</c> is the trustworthy signal, and a per-turn OS code describes one turn, not the
    /// agent).</summary>
    int? ExitCode { get; }

    /// <summary>
    /// Reads this turn's stdout NDJSON lines as they arrive, ending when stdout hits EOF (the
    /// process exited, or is about to). Must not throw for a normal EOF — the sequence simply ends;
    /// it throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> is cancelled
    /// first (the runtime's per-turn deadline or owner-cancel path).
    /// </summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct);

    /// <summary>Wait up to <paramref name="timeout"/> for this turn's process to exit (returns silently on timeout).</summary>
    Task WaitForExitAsync(TimeSpan? timeout = null);

    /// <summary>Kill this turn's process tree, then wait up to <paramref name="timeout"/> for it to exit.
    /// Must be safe to call even after <see cref="IAsyncDisposable.DisposeAsync"/> has already run —
    /// see this interface's class doc.</summary>
    Task TerminateAsync(TimeSpan? timeout = null);
}
