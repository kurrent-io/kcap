using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>
/// <see cref="IMuseMspProcess"/> over a real <see cref="Process"/>. Stdin stays open for the child's
/// lifetime. Stderr text is never logged: it can carry prompt fragments, paths or auth detail.
/// </summary>
internal sealed partial class MuseMspProcess : IMuseMspProcess {
    /// <summary>Over-cap stderr is dropped, never rotated: the explanation of a failed launch is at the start.</summary>
    internal const int DiagnosticsCap = 4096;

    readonly ILogger             _logger;
    readonly Lock                _diagnosticsGate = new();
    readonly StringBuilder       _diagnostics     = new();
    readonly ManagedChildProcess _child;

    internal MuseMspProcess(ProcessStartInfo psi, ILogger logger, TimeProvider time)
        : this(Process.Start(psi) ?? throw new InvalidOperationException(
                   $"muse_msp_spawn_failed: '{psi.FileName}' did not start (Process.Start returned null)."),
               logger, time) { }

    internal MuseMspProcess(Process process, ILogger logger, TimeProvider time) {
        _logger = logger;
        _child  = new ManagedChildProcess(process, time, OnStderrLine);
    }

    public int  Pid       => _child.Pid;
    public bool HasExited => _child.HasExited;
    public int? ExitCode  => _child.ExitCode;

    public string? Diagnostics {
        get { lock (_diagnosticsGate) return _diagnostics.Length == 0 ? null : _diagnostics.ToString(); }
    }

    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct) => _child.ReadStdoutLinesAsync(ct);

    public Task WriteLineAsync(string json, CancellationToken ct) => _child.WriteLineAsync(json, ct);

    void OnStderrLine(string line) {
        lock (_diagnosticsGate) AppendCapped(_diagnostics, line);
        LogStderrShape(line.Length);
    }

    /// <summary>A line that would cross the cap is cut at it.</summary>
    internal static void AppendCapped(StringBuilder diagnostics, string line) {
        var room = DiagnosticsCap - diagnostics.Length;
        if (room <= 0) return;

        if (line.Length < room) diagnostics.Append(line).Append('\n');
        else diagnostics.Append(line, 0, room);
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => _child.WaitForExitAsync(timeout);

    public Task CloseInputAsync(TimeSpan timeout) => _child.CloseInputAsync(timeout);

    public Task TerminateAsync(TimeSpan? timeout = null) => _child.TerminateAsync(timeout);

    public ValueTask DisposeAsync() => _child.DisposeAsync();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Muse MSP stderr: {Length} chars")]
    partial void LogStderrShape(int length);
}
