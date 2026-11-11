namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// ACP uses stdio for JSON-RPC rather than terminal bytes; AcpConnection owns that traffic.
/// Keep process lifetime separate so runtime tests can supply a child without spawning a real process.
/// </summary>
internal interface IAcpProcess : IAsyncDisposable {
    /// <summary>OS process id of the hosted <c>cursor-agent acp</c> process.</summary>
    int Pid { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>Exit code once <see cref="HasExited"/>; null while running or if unknown.</summary>
    int? ExitCode { get; }

    /// <summary>Wait up to <paramref name="timeout"/> for the process to exit (returns silently on timeout).</summary>
    Task WaitForExitAsync(TimeSpan? timeout = null);

    /// <summary>Terminate the process (SIGTERM then SIGKILL) within <paramref name="timeout"/>.</summary>
    Task TerminateAsync(TimeSpan? timeout = null);

    /// <summary>A bounded capture of whatever the child wrote to stderr, or null if it wrote nothing.
    /// Read when a launch fails or a turn goes silent, to turn "the agent did nothing" into a reason:
    /// a vendor waiting out a rate limit or an expired credential explains itself here and nowhere
    /// else on the wire. Null for a double with no real child. Mirrors <c>IPiRpcProcess</c>.</summary>
    string? Diagnostics => null;
}
