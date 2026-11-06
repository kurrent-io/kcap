using System.Diagnostics;
using System.Globalization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// A real <c>kcap watch</c> process as the spawner leaves it: detached from the test, with a pid file naming
/// this incarnation. Disposing kills whatever a failed test left running.
/// </summary>
public sealed class WatchedProcess : IAsyncDisposable {
    static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(20);

    readonly Process      _process;
    readonly Task<string> _stderr;
    readonly string       _logPath;

    WatchedProcess(Process process, string sessionId, string watchersDir, string logPath) {
        _process    = process;
        _logPath    = logPath;
        SessionId   = sessionId;
        PidFile     = Path.Combine(watchersDir, $"{sessionId}.pid");
        _           = process.StandardOutput.ReadToEndAsync();
        _stderr     = process.StandardError.ReadToEndAsync();
    }

    public string SessionId { get; }

    public string PidFile { get; }

    public int Pid => _process.Id;

    /// <param name="watchersDir">Where the watcher keeps its pid and heartbeat files.</param>
    public static async Task<WatchedProcess> StartAsync(
            TempDaemonStore daemons, TempConfigRoot config, TempHome home, string watchersDir,
            string serverUrl, string sessionId, string transcript, string vendor = "claude") {
        var psi = KcapProcess.StartInfo(daemons.Store, config.Root, "watch", sessionId, transcript, "--skip-title", "--vendor", vendor);
        psi.Environment["HOME"]                  = home.Path;
        psi.Environment[ProfileOverrides.UrlVar] = serverUrl;
        psi.Environment[WatcherPaths.DirEnvVar]  = watchersDir;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap");
        process.StandardInput.Close();

        var watched = new WatchedProcess(process, sessionId, watchersDir, Path.Combine(config.Directory, "logs", $"{sessionId}.log"));

        // What the spawner writes, so the watcher can retire its own file on a clean exit.
        Directory.CreateDirectory(watchersDir);
        await File.WriteAllTextAsync(watched.PidFile, $"{process.Id}\n{ProcessStartToken.ForPid(process.Id)}");

        return watched;
    }

    /// <summary>What the watcher has logged so far; empty until it opens its log.</summary>
    public string Log() {
        try {
            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        } catch (IOException) {
            return "";
        } catch (UnauthorizedAccessException) {
            return "";
        }
    }

    /// <summary>Polls the watcher's log to the shared deadline.</summary>
    public async Task WaitForLogAsync(string text, string step) {
        using var deadline = new CancellationTokenSource(BoundedWait.Deadline);

        while (!Log().Contains(text, StringComparison.Ordinal)) {
            try {
                await Task.Delay(PollGap, deadline.Token);
            } catch (OperationCanceledException) {
                throw Stalled(step);
            }
        }
    }

    public void SignalTerm() {
        using var kill = Process.Start(new ProcessStartInfo("/bin/kill", ["-TERM", Pid.ToString(CultureInfo.InvariantCulture)]) { UseShellExecute = false })
                      ?? throw new InvalidOperationException("Failed to start /bin/kill");
        kill.WaitForExit();
    }

    /// <summary>The exit code, once the process leaves.</summary>
    public async Task<int> WaitForExitAsync(string step) {
        try {
            await _process.WaitForExitAsync().WaitAsync(BoundedWait.Deadline);
        } catch (TimeoutException) {
            throw Stalled(step);
        }

        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync() {
        try {
            if (!_process.HasExited) _process.Kill();
        } catch (InvalidOperationException) { }

        await _process.WaitForExitAsync();
        _process.Dispose();
    }

    TimeoutException Stalled(string step) =>
        new($"Stalled waiting for {step}: not done within {BoundedWait.DeadlineSeconds}s.\n--- watcher log ---\n{Log()}\n--- stderr ---\n{(_stderr.IsCompletedSuccessfully ? _stderr.Result : "(still open)")}");
}
