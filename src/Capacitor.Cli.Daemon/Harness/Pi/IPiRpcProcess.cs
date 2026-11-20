using Capacitor.Cli.Daemon.Harness.Antigravity;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>
/// A long-lived hosted <c>pi</c> child speaking stdio JSONL RPC. Unlike <see cref="IAgyTurnProcess"/>, its
/// stdin stays open for the session.
/// <see cref="PiRpcProcess"/> implements it.
/// <para>A real implementation must honour:</para>
/// <para>0. <see cref="IAsyncDisposable.DisposeAsync"/> may kill a running child, so it is not exit
/// evidence: read <see cref="HasExited"/> before disposing.</para>
/// <para>1. <see cref="IAsyncDisposable.DisposeAsync"/> is idempotent.</para>
/// <para>2. <see cref="TerminateAsync"/> is safe after disposal.</para>
/// <para>3. <see cref="WriteLineAsync"/> calls are serialized: a torn line corrupts the wire for every
/// later command.</para>
/// </summary>
internal interface IPiRpcProcess : IAsyncDisposable {
    /// <summary>OS process id of the hosted <c>pi</c> child.</summary>
    int Pid { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>OS exit code once <see cref="HasExited"/>; null while running or if unknown.</summary>
    int? ExitCode { get; }

    /// <summary>A bounded capture of whatever the child wrote to stderr, or null if it wrote
    /// nothing. Read on a failed launch (see <c>PiRpcHostedAgentRuntimeFactory</c>'s post-spawn
    /// catch) or an unexpected exit, to turn silence into a reason an operator can act on.</summary>
    string? Diagnostics { get; }

    /// <summary>
    /// Reads this child's stdout, LF-framed, one JSONL line at a time, ending when stdout hits EOF
    /// (the process exited, or is about to). Must not throw for a normal EOF — the sequence simply
    /// ends; it throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> is
    /// cancelled first.
    /// </summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="json"/> followed by a single <c>\n</c> to the child's stdin and
    /// flushes, so the line is on the wire before this returns. Concurrent callers are serialized
    /// against each other (contract 3 on this interface's class doc) — never call this expecting an
    /// implementation to accept interleaved writes.
    /// </summary>
    Task WriteLineAsync(string json, CancellationToken ct);

    /// <summary>Wait up to <paramref name="timeout"/> for the process to exit (returns silently on timeout).</summary>
    Task WaitForExitAsync(TimeSpan? timeout = null);

    /// <summary>Closes the child's stdin, which Pi treats as the end of its session. Bounded by
    /// <paramref name="timeout"/> when a write already holds stdin, and a no-op once closed.</summary>
    Task CloseInputAsync(TimeSpan timeout);

    /// <summary>Terminate the process — an immediate kill of the whole process tree, matching
    /// <c>AgyTurnProcess</c> (not a graceful signal first) — within <paramref name="timeout"/>. Must
    /// be safe to call even after <see cref="IAsyncDisposable.DisposeAsync"/> has already run — see
    /// this interface's class doc.</summary>
    Task TerminateAsync(TimeSpan? timeout = null);
}
