using System.Diagnostics;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// <see cref="IAcpProcess"/> over a real <see cref="Process"/>, the ACP child spawned by
/// <see cref="Services.AcpHostedAgentRuntimeFactory"/>. Stdin and stdout carry JSON-RPC frames read by
/// <see cref="AcpConnection"/>; this type owns the lifecycle and the stderr drain. Stderr is logged by
/// length only unless <c>KCAP_ACP_DEBUG_FRAMES</c> (<see cref="DaemonConfig.DebugFrames"/>) is on: it
/// can carry paths, prompt fragments and error detail.
/// </summary>
internal sealed partial class AcpChildProcess : IAcpProcess {
    readonly ILogger             _logger;
    readonly bool                _debugFrames;
    readonly string              _vendor;
    readonly Lock                _diagnosticsGate = new();
    readonly Queue<string>       _diagnostics     = new();
    readonly ManagedChildProcess _child;

    int _diagnosticsLength;

    /// <summary>Enough to carry a startup/auth error or a retry banner and the context around it, and
    /// small enough that a chatty long-lived child cannot grow the daemon's heap over a whole
    /// session.</summary>
    const int DiagnosticsCap = 4096;

    /// <summary>The most recent stderr within the cap, oldest lines evicted first. Recency rather
    /// than a first-N buffer because the two readers want different moments and only recency serves
    /// both: a failed launch has written nothing else yet, while a turn that goes silent an hour in
    /// would otherwise be explained by startup chatter that filled the buffer and never left.</summary>
    public string? Diagnostics {
        get { lock (_diagnosticsGate) return _diagnostics.Count == 0 ? null : string.Join('\n', _diagnostics); }
    }

    public AcpChildProcess(
            Process process, ILogger logger, TimeProvider time, bool debugFrames = false,
            string vendor = "cursor") {
        _logger      = logger;
        _debugFrames = debugFrames;
        _vendor      = vendor;
        _child       = new ManagedChildProcess(process, time, OnStderrLine);
    }

    void OnStderrLine(string line) {
        // Retained whatever the log level does: a vendor that goes silent on the wire writes its
        // whole diagnosis here — a rate-limit retry banner reaches stderr and nowhere else.
        // Daemon-local; never forwarded.
        Capture(line);

        if (_debugFrames)
            LogStderrLineFull(_vendor, AcpDebugFrameLog.Cap(line));
        else
            LogStderrLineShape(_vendor, line.Length);
    }

    void Capture(string line) {
        // Truncated before it is stored, not merely gated on the running total: one enormous line —
        // a stack trace, a base64 payload — would otherwise blow the cap on its own.
        var kept = line.Length > DiagnosticsCap ? line[..DiagnosticsCap] : line;

        lock (_diagnosticsGate) {
            _diagnostics.Enqueue(kept);
            _diagnosticsLength += kept.Length + 1;

            while (_diagnosticsLength > DiagnosticsCap && _diagnostics.Count > 1)
                _diagnosticsLength -= _diagnostics.Dequeue().Length + 1;
        }
    }

    /// <summary>Zero once the child has exited.</summary>
    public int Pid => _child.HasExited ? 0 : _child.Pid;

    public bool HasExited => _child.HasExited;

    public int? ExitCode => _child.ExitCode;

    public Task WaitForExitAsync(TimeSpan? timeout = null) => _child.WaitForExitAsync(timeout);

    public Task TerminateAsync(TimeSpan? timeout = null) => _child.TerminateAsync(timeout);

    public ValueTask DisposeAsync() => _child.DisposeAsync();

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Vendor} stderr: {Line}")]
    partial void LogStderrLineFull(string vendor, string line);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Vendor} stderr: {Length} chars")]
    partial void LogStderrLineShape(string vendor, int length);
}
