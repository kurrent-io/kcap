using System.Diagnostics;
using System.Text;

namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>
/// Runs one headless harness process to its end: optional stdin, stdout and stderr streamed line by line,
/// an absolute deadline, and external cancellation. Every kill is waited out, with a bound, so the caller can
/// remove the run's directory once this returns or throws.
/// </summary>
static class HeadlessProcess {
    static readonly TimeSpan AfterKill = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(1);

    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Starts <paramref name="psi"/> with every stream redirected as UTF-8 (stdin without a BOM). With
    /// <paramref name="stdin"/> null, stdin is closed at once. <paramref name="onStderr"/> returning true kills
    /// the process. Returns null when it could not be started. The deadline kills it and returns
    /// <see cref="HeadlessProcessExit.TimedOut"/>; external cancellation kills it and propagates.
    /// </summary>
    public static Task<HeadlessProcessExit?> RunAsync(
            ProcessStartInfo    psi,
            string?             stdin,
            Action<string>      onStdout,
            Func<string, bool>? onStderr,
            TimeSpan            timeout,
            TimeProvider        time,
            Action<string>      log,
            CancellationToken   ct
        ) =>
        RunAsync(psi, async (input, token) => {
            if (stdin is not null) {
                await input.WriteAsync(stdin.AsMemory(), token);
                await input.FlushAsync(token);
            }
        }, onStdout, onStderr, timeout, time, log, ct);

    /// <summary>As the string overload, with <paramref name="writeInput"/> writing stdin, which is closed after it.</summary>
    internal static async Task<HeadlessProcessExit?> RunAsync(
            ProcessStartInfo                           psi,
            Func<StreamWriter, CancellationToken, Task> writeInput,
            Action<string>      onStdout,
            Func<string, bool>? onStderr,
            TimeSpan            timeout,
            TimeProvider        time,
            Action<string>      log,
            CancellationToken   ct
        ) {
        psi.RedirectStandardInput  = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.StandardInputEncoding  = Utf8NoBom;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding  = Encoding.UTF8;
        psi.UseShellExecute        = false;
        psi.CreateNoWindow         = true;

        var name = Path.GetFileNameWithoutExtension(psi.FileName);

        using var process = CliProcess.TryStart(psi, log);

        if (process is null) return null;

        var stderr     = new StderrTail();
        var killEarly  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Read without a token so the lines seen before a kill survive it.
        var stdoutTask = ReadLinesAsync(process.StandardOutput, onStdout);
        var stderrTask = ReadLinesAsync(process.StandardError, line => {
            stderr.Append(line);
            if (onStderr?.Invoke(line) == true) killEarly.TrySetResult();
        });

        using var timeoutCts = new CancellationTokenSource(timeout, time);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            try {
                await writeInput(process.StandardInput, linkedCts.Token);

                process.StandardInput.Close();
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // A process that exits before reading its input breaks the pipe; what it said on the way out is
                // still the outcome. Any failed write kills the child first, so it never outlives the run.
                log($"{name} could not be given its input: {ForLog(ex.Message)}");

                try { process.StandardInput.Dispose(); } catch { /* the pipe is already broken */ }

                // A broken pipe usually means the child is exiting; its own exit code is the outcome only if
                // it ends before the kill does.
                var exitedOnItsOwn = await ExitCodeWithinAsync(process, ExitGrace, time);
                await KillAsync(process, stdoutTask, stderrTask, time);
                ct.ThrowIfCancellationRequested();

                if (ex is not IOException) throw;

                return new(exitedOnItsOwn, timeoutCts.IsCancellationRequested && exitedOnItsOwn is null, false, stderr.Text);
            }

            var exited = process.WaitForExitAsync(linkedCts.Token);

            if (await Task.WhenAny(exited, killEarly.Task) != exited) {
                await KillAsync(process, stdoutTask, stderrTask, time);

                return new(null, false, true, stderr.Text);
            }

            await exited;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            await KillAsync(process, stdoutTask, stderrTask, time);

            throw;
        } catch (OperationCanceledException) {
            log($"{name} timed out ({timeout.TotalSeconds:0}s), killing");
            await KillAsync(process, stdoutTask, stderrTask, time);

            return new(null, true, false, stderr.Text);
        }

        try {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linkedCts.Token);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            CloseOutput(process);

            throw;
        } catch (OperationCanceledException) {
            // A descendant that outlived the process holds the pipe; the process itself has written all it will.
            log($"{name} exited but its output stayed open to the deadline; using what was read");
            CloseOutput(process);
        }

        return new(process.ExitCode, false, false, stderr.Text);
    }

    /// <summary>Folds a multi-line value onto one log line.</summary>
    public static string ForLog(string value) {
        var sb = new StringBuilder(value.Length);

        foreach (var ch in value) {
            if (ch is '\r' or '\n') {
                sb.Append("\\n");
                continue;
            }

            if (char.IsControl(ch)) continue;

            sb.Append(ch);
        }

        return sb.ToString();
    }

    static async Task<int?> ExitCodeWithinAsync(Process process, TimeSpan grace, TimeProvider time) {
        try {
            await process.WaitForExitAsync().WaitAsync(grace, time);

            return process.ExitCode;
        } catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) {
            return null;
        }
    }

    static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine) {
        try {
            while (await reader.ReadLineAsync() is { } line) {
                onLine(line);
            }
        } catch (Exception e) when (e is ObjectDisposedException or IOException) { }
    }

    /// <summary>A grandchild that inherited the output pipes can hold them open past the kill, so every wait is
    /// bounded.</summary>
    static async Task KillAsync(Process process, Task stdoutTask, Task stderrTask, TimeProvider time) {
        try { ProcessTree.Kill(process); } catch {
            /* already gone */
        }

        try {
            await process.WaitForExitAsync().WaitAsync(AfterKill, time);
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(AfterKill, time);
        } catch {
            CloseOutput(process);
        }
    }

    /// <summary>Ends reads a descendant would otherwise keep pending: the process does not dispose its own readers.</summary>
    static void CloseOutput(Process process) {
        try { process.StandardOutput.Dispose(); } catch { /* best effort */ }
        try { process.StandardError.Dispose(); } catch { /* best effort */ }
    }
}
