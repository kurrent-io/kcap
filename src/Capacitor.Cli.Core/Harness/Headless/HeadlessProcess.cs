using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>
/// Runs one headless harness process to its end: optional stdin, stdout and stderr streamed line by line,
/// an absolute deadline, and external cancellation. Every kill is waited out, with a bound, so the caller can
/// remove the run's directory once this returns or throws.
/// </summary>
static class HeadlessProcess {
    static readonly TimeSpan AfterKill   = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ExitGrace   = TimeSpan.FromSeconds(1);
    static readonly TimeSpan TrackPeriod = TimeSpan.FromMilliseconds(500);

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
        using var survivors  = new Survivors(process, time);

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
            survivors.Stop();
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
            survivors.Kill();
            CloseOutput(process);

            throw;
        } catch (OperationCanceledException) {
            // A descendant that outlived the process holds the pipe, and may still be writing into the run directory.
            log($"{name} exited but a descendant kept its output open to the deadline, killing it");
            survivors.Kill();
            CloseOutput(process);

            return new(null, true, false, stderr.Text);
        }

        return new(process.ExitCode, false, false, stderr.Text);
    }

    /// <summary>The process's descendants, listed every <see cref="TrackPeriod"/> while it runs, so the ones that
    /// outlive it can be killed after they are reparented out of its tree. A descendant forked and orphaned
    /// between two listings is missed. On Windows, where nothing is listed, the runtime's tree kill finds them
    /// through the exited process's id.</summary>
    sealed class Survivors : IDisposable {
        readonly Process                           _process;
        readonly ConcurrentDictionary<int, string> _seen = new();
        readonly CancellationTokenSource           _stop = new();

        public Survivors(Process process, TimeProvider time) {
            _process = process;

            if (!OperatingSystem.IsWindows() && ProcessStartToken.ForPid(process.Id) is { } identity)
                _ = TrackAsync(process.Id, identity, time, _stop.Token);
        }

        /// <summary>Ends the listing once the process has exited; what was seen stays to be killed.</summary>
        public void Stop() => _stop.Cancel();

        public void Dispose() {
            _stop.Cancel();
            _stop.Dispose();
        }

        async Task TrackAsync(int pid, string identity, TimeProvider time, CancellationToken stop) {
            try {
                while (!stop.IsCancellationRequested) {
                    foreach (var (child, childIdentity) in ProcessTree.Descendants(pid, identity)) _seen.TryAdd(child, childIdentity);

                    await Task.Delay(TrackPeriod, time, stop);
                }
            } catch (Exception) {
                // Stopped, or a listing failed: what was seen so far is still killed.
            }
        }

        public void Kill() {
            if (OperatingSystem.IsWindows()) {
                try {
#pragma warning disable RS0030 // Windows has no process groups to hang up; ProcessTree.Kill skips an exited root
                    _process.Kill(entireProcessTree: true);
#pragma warning restore RS0030
                } catch (Exception) {
                    // Nothing reachable is left.
                }

                return;
            }

            foreach (var (pid, identity) in _seen) {
                try { ProcessTree.Kill(pid, identity); } catch (Exception) { /* already gone */ }
            }
        }
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
