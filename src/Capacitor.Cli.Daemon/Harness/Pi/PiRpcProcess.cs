using System.Diagnostics;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>
/// <see cref="IPiRpcProcess"/> over a real <see cref="Process"/>: the long-lived <c>pi</c> child
/// backing one hosted Pi session. Stdin stays open for the whole session so the runtime can keep
/// sending commands. Stderr text is never logged: it can carry prompt fragments, paths or auth detail.
/// </summary>
internal sealed partial class PiRpcProcess : IPiRpcProcess {
    /// <summary>Small enough that a chatty long-lived child cannot grow the daemon's heap over a
    /// session.</summary>
    const int DiagnosticsCap = 4096;

    readonly ILogger             _logger;
    readonly StderrHead          _diagnostics = new(DiagnosticsCap);
    readonly ManagedChildProcess _child;

    internal PiRpcProcess(ProcessStartInfo psi, ILogger logger, TimeProvider time)
        : this(Process.Start(psi) ?? throw new InvalidOperationException(
                   $"pi_rpc_spawn_failed: '{psi.FileName}' did not start (Process.Start returned null)."),
               logger, time) { }

    internal PiRpcProcess(Process process, ILogger logger, TimeProvider time) {
        _logger = logger;
        _child  = new ManagedChildProcess(process, time, OnStderrLine);
    }

    public int  Pid       => _child.Pid;
    public bool HasExited => _child.HasExited;
    public int? ExitCode  => _child.ExitCode;

    public string? Diagnostics => _diagnostics.Text;

    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct) => _child.ReadStdoutLinesAsync(ct);

    public Task WriteLineAsync(string json, CancellationToken ct) => _child.WriteLineAsync(json, ct);

    void OnStderrLine(string line) {
        _diagnostics.Capture(line);
        LogStderrShape(line.Length);
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => _child.WaitForExitAsync(timeout);

    public Task CloseInputAsync(TimeSpan timeout) => _child.CloseInputAsync(timeout);

    public Task TerminateAsync(TimeSpan? timeout = null) => _child.TerminateAsync(timeout);

    public ValueTask DisposeAsync() => _child.DisposeAsync();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pi RPC stderr: {Length} chars")]
    partial void LogStderrShape(int length);
}
