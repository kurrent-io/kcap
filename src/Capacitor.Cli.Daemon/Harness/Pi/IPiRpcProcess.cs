using Capacitor.Cli.Daemon.Harness.Antigravity;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>
/// Minimal process-lifecycle abstraction for a hosted Pi <c>pi</c> child speaking the stdio JSONL
/// RPC protocol (see <c>PiRpc</c>). Unlike <see cref="IAgyTurnProcess"/> — an exec-per-turn child
/// whose stdin is closed the instant it is spawned — a Pi RPC child is LONG-LIVED: one process backs
/// the whole hosted session, and its stdin stays open for that entire lifetime so the runtime can
/// keep sending <c>prompt</c>/<c>abort</c>/<c>get_state</c>/<c>set_model</c> commands to it turn
/// after turn. That is exactly the capability this interface adds over <see cref="IAgyTurnProcess"/>:
/// <see cref="WriteLineAsync"/>.
///
/// <para>Exists so a Pi hosted-agent runtime is testable without spawning a real process;
/// <see cref="PiRpcProcess"/> implements it over <see cref="System.Diagnostics.Process"/>.</para>
///
/// <para><b>Two contracts a real implementation must honor — copied verbatim from
/// <see cref="IAgyTurnProcess"/>, because the same runtime shapes (a turn worker's own
/// <c>finally</c> racing the runtime's own teardown) apply here too:</b></para>
/// <para>0. <see cref="IAsyncDisposable.DisposeAsync"/> MAY terminate a still-running child, and an
/// implementation over a real process does. It is therefore NOT a source of exit evidence: a caller
/// that needs to know whether a child exited must terminate it explicitly and read
/// <see cref="HasExited"/> while the handle is still valid, BEFORE disposing — a disposed process
/// object reports exited whether or not it is.</para>
/// <para>1. <see cref="IAsyncDisposable.DisposeAsync"/> MUST be idempotent. A second call on the same
/// instance must be a safe no-op, never throw.</para>
/// <para>2. <see cref="TerminateAsync"/> MUST be safe to call AFTER
/// <see cref="IAsyncDisposable.DisposeAsync"/> has already run — this must degrade gracefully (a
/// no-op, or a caught/logged failure internally), never throw an exception a caller doesn't already
/// catch.</para>
/// <para>3. <see cref="WriteLineAsync"/> calls MUST be serialized against each other so that two
/// concurrent writers can never interleave partial lines on the child's stdin — a torn line is
/// unparseable JSON on the other end, and unlike a read race (which just ends the enumerable early),
/// a write race corrupts the wire for every command after it.</para>
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
