using System.Diagnostics;
using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Antigravity;

/// <summary>
/// <see cref="IAgyTurnProcess"/> over a real <see cref="Process"/>: ONE <c>agy -p</c> turn. Stderr text
/// is never logged: it can carry prompt fragments, paths and, on the auth path, a URL bearing a login
/// code.
/// </summary>
internal sealed partial class AgyTurnProcess : IAgyTurnProcess, IAgyTurnDiagnostics {
    /// <summary>Enough to carry an auth error and the URL under it, and small enough that a chatty
    /// child cannot grow the daemon's heap one turn at a time.</summary>
    const int DiagnosticsCap = 4096;

    readonly ILogger             _logger;
    readonly StderrHead          _diagnostics = new(DiagnosticsCap);
    readonly ManagedChildProcess _child;

    internal AgyTurnProcess(ProcessStartInfo psi, ILogger logger, TimeProvider time)
        : this(Process.Start(psi) ?? throw new InvalidOperationException(
                   $"antigravity_turn_spawn_failed: '{psi.FileName}' did not start (Process.Start returned null)."),
               logger, time) { }

    internal AgyTurnProcess(Process process, ILogger logger, TimeProvider time) {
        _logger = logger;

        // A containment decision: an unauthenticated agy prints an OAuth URL and waits for a pasted
        // code, and a child with no readable stdin cannot be handed one. The launch deadline bounds it.
        try {
            process.StandardInput.Close();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Antigravity: could not close a turn child's stdin.");
        }

        _child = new ManagedChildProcess(process, time, OnStderrLine);
    }

    public int  Pid       => _child.Pid;
    public bool HasExited => _child.HasExited;
    public int? ExitCode  => _child.ExitCode;

    public string? Diagnostics => _diagnostics.Text;

    /// <summary>
    /// This turn's stdout NDJSON, line by line, ending at EOF. Lines that are not JSON are still
    /// yielded (the runtime's parser drops them) and also captured as diagnostics: agy reports
    /// "authentication required" as plain text on this stream, not on stderr.
    /// </summary>
    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct) {
        await foreach (var line in _child.ReadStdoutLinesAsync(ct).ConfigureAwait(false)) {
            if (!line.StartsWith('{')) _diagnostics.Capture(line);

            yield return line;
        }
    }

    void OnStderrLine(string line) {
        _diagnostics.Capture(line);
        LogStderrShape(line.Length);
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => _child.WaitForExitAsync(timeout);

    public Task TerminateAsync(TimeSpan? timeout = null) => _child.TerminateAsync(timeout);

    public ValueTask DisposeAsync() => _child.DisposeAsync();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Antigravity turn stderr: {Length} chars")]
    partial void LogStderrShape(int length);
}
