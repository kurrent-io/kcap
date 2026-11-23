using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Antigravity;

/// <summary>
/// <see cref="IAgyTurnProcess"/> over a real <see cref="Process"/>: ONE <c>agy -p</c> turn, handed its
/// prompt on stdin. Stderr text is never logged: it can carry prompt fragments, paths and, on the auth
/// path, a URL bearing a login code.
/// </summary>
internal sealed partial class AgyTurnProcess : IAgyTurnProcess, IAgyTurnDiagnostics {
    /// <summary>Enough to carry an auth error and the URL under it, and small enough that a chatty
    /// child cannot grow the daemon's heap one turn at a time.</summary>
    const int DiagnosticsCap = 4096;

    static readonly TimeSpan InputCloseWait = TimeSpan.FromSeconds(5);

    readonly ILogger             _logger;
    readonly StderrHead          _diagnostics = new(DiagnosticsCap);
    readonly ManagedChildProcess _child;

    internal AgyTurnProcess(ProcessStartInfo psi, string prompt, ILogger logger, TimeProvider time)
        : this(Process.Start(psi) ?? throw new InvalidOperationException(
                   $"antigravity_turn_spawn_failed: '{psi.FileName}' did not start (Process.Start returned null)."),
               prompt, logger, time) { }

    internal AgyTurnProcess(Process process, string prompt, ILogger logger, TimeProvider time) {
        _logger = logger;
        _child  = new ManagedChildProcess(process, time, OnStderrLine);

        // Off the caller's thread: a pipe write blocks until the child reads, and a prompt can be larger
        // than the pipe's buffer.
        _ = Task.Run(() => DeliverPromptAsync(prompt));
    }

    /// <summary>
    /// The one stdin line a turn child reads: agy's stream-json user message. Every non-ASCII character
    /// is escaped, so the line is the same bytes in whatever code page the child's stdin was opened with.
    /// </summary>
    internal static string InputLine(string prompt) {
        using var buffer = new MemoryStream();

        using (var json = new Utf8JsonWriter(buffer)) {
            json.WriteStartObject();
            json.WriteString("event", "user");
            json.WriteStartObject("message");
            json.WriteString("role", "user");
            json.WriteString("content", prompt);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Writes the prompt and closes stdin. The close is what ends the turn's input, so agy runs this
    /// one message and exits, and it leaves nothing open for an unauthenticated agy to wait on. A child
    /// that exits or is killed before reading breaks the pipe; its exit is what reports that failure.
    /// </summary>
    async Task DeliverPromptAsync(string prompt) {
        try {
            await _child.WriteLineAsync(InputLine(prompt), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Antigravity: a turn child did not take its prompt.");
        }

        await _child.CloseInputAsync(InputCloseWait).ConfigureAwait(false);
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
