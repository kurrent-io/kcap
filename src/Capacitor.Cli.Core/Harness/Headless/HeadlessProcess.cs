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
    static readonly TimeSpan GonePoll    = TimeSpan.FromMilliseconds(20);

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
            return await RunToEndAsync();
        } finally {
            // Every path ends here, so nothing the process started outlives the run or holds its directory.
            await survivors.KillAsync(AfterKill);
        }

        async Task<HeadlessProcessExit> RunToEndAsync() {
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
                    await KillAsync(process, survivors, stdoutTask, stderrTask, time);
                    ct.ThrowIfCancellationRequested();

                    if (ex is not IOException) throw;

                    return new(exitedOnItsOwn, timeoutCts.IsCancellationRequested && exitedOnItsOwn is null, false, stderr.Text);
                }

                var exited = process.WaitForExitAsync(linkedCts.Token);

                if (await Task.WhenAny(exited, killEarly.Task) != exited) {
                    await KillAsync(process, survivors, stdoutTask, stderrTask, time);

                    return new(null, false, true, stderr.Text);
                }

                await exited;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                await KillAsync(process, survivors, stdoutTask, stderrTask, time);

                throw;
            } catch (OperationCanceledException) {
                log($"{name} timed out ({timeout.TotalSeconds:0}s), killing");
                await KillAsync(process, survivors, stdoutTask, stderrTask, time);

                return new(null, true, false, stderr.Text);
            }

            try {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linkedCts.Token);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                await KillAsync(process, survivors, stdoutTask, stderrTask, time);

                throw;
            } catch (OperationCanceledException) {
                // A descendant that outlived the process holds the pipe, and may still be writing into the run directory.
                log($"{name} exited but a descendant kept its output open to the deadline, killing it");
                await KillAsync(process, survivors, stdoutTask, stderrTask, time);

                return new(null, true, false, stderr.Text);
            }

            return new(process.ExitCode, false, false, stderr.Text);
        }
    }

    /// <summary>The process's descendants, listed every <see cref="TrackPeriod"/> while it runs, so the ones that
    /// outlive it can still be killed once its exit has reparented them out of its tree. A descendant forked and
    /// orphaned between two listings is missed.</summary>
    sealed class Survivors : IDisposable {
        readonly TimeProvider                      _time;
        readonly ConcurrentDictionary<int, string> _seen = new();
        readonly CancellationTokenSource           _stop = new();

        public Survivors(Process process, TimeProvider time) {
            _time = time;

            if (ProcessStartToken.ForPid(process.Id) is { } identity)
                _ = TrackAsync(process.Id, identity, _stop.Token);
        }

        public void Dispose() {
            _stop.Cancel();
            _stop.Dispose();
        }

        async Task TrackAsync(int pid, string identity, CancellationToken stop) {
            try {
                while (!stop.IsCancellationRequested) {
                    foreach (var (child, childIdentity) in ProcessTree.Descendants(pid, identity)) _seen.TryAdd(child, childIdentity);

                    await Task.Delay(TrackPeriod, _time, stop);
                }
            } catch (Exception) {
                // Stopped, or a listing failed: what was seen so far is still killed.
            }
        }

        /// <summary>Kills every descendant seen that is still the same process, then waits up to
        /// <paramref name="budget"/> for each to be gone: a kill can return with termination still pending, and a
        /// process still alive can keep the run directory from being removed.</summary>
        public async Task KillAsync(TimeSpan budget) {
            _stop.Cancel();

            List<(int Pid, string Identity, Process? Handle)> killed = [];

            try {
                foreach (var (pid, identity) in _seen) {
                    // On Windows a handle opened before the kill is what can be waited on once it returns.
                    var handle = OperatingSystem.IsWindows() ? OpenHandle(pid, identity) : null;

                    if (ProcessStartToken.Matches(pid, identity) != true) {
                        handle?.Dispose();
                        continue;
                    }

                    try { ProcessTree.Kill(pid, identity); } catch (Exception) { /* already gone */ }

                    killed.Add((pid, identity, handle));
                }

                // Real time: these are real processes, and a test's fake clock would never end the wait.
                var start = TimeProvider.System.GetTimestamp();

                foreach (var (pid, identity, handle) in killed) {
                    var left = budget - TimeProvider.System.GetElapsedTime(start);
                    if (left <= TimeSpan.Zero) return;

                    if (handle is not null) {
                        try { await handle.WaitForExitAsync().WaitAsync(left, TimeProvider.System); } catch (Exception) { /* bounded */ }

                        continue;
                    }

                    while (ProcessStartToken.Matches(pid, identity) == true && TimeProvider.System.GetElapsedTime(start) < budget)
                        await Task.Delay(GonePoll, TimeProvider.System);
                }
            } finally {
                foreach (var (_, _, handle) in killed) handle?.Dispose();
            }
        }

        static Process? OpenHandle(int pid, string identity) {
            try {
                var process = Process.GetProcessById(pid);
                _ = process.Handle;

                if ($"tk:{process.StartTime.ToUniversalTime().Ticks}" == identity) return process;

                process.Dispose();
            } catch (Exception) {
                // Gone already.
            }

            return null;
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
    static async Task KillAsync(Process process, Survivors survivors, Task stdoutTask, Task stderrTask, TimeProvider time) {
        try { ProcessTree.Kill(process); } catch {
            /* already gone */
        }

        // Before the output wait: a descendant still holding the pipes would otherwise run it to its bound.
        await survivors.KillAsync(AfterKill);

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
