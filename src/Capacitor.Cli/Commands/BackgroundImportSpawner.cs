using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>Spawns the detached remainder import with its streams redirected and handles not
/// inherited, then waits for the child to say it accepted its work or to exit. Its stdout stays open
/// only for that one line.</summary>
sealed class BackgroundImportSpawner(ConfigRoot config, IProcessStarter starter, TimeProvider time) : IBackgroundImportSpawner {
    /// <summary>Only ends the wait: the child signals before any import work, so a child still silent
    /// here is slow or stuck, which setup cannot tell apart.</summary>
    static readonly TimeSpan Readiness = TimeSpan.FromSeconds(15);

    public BackgroundImportLaunch Spawn(BackgroundImportRequest request) {
        var logPath = config.Path($"import-{request.RunId}.log");
        try {
            using (OwnerOnlyFile.CreateNew(logPath)) { }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, null, $"could not create {logPath}: {ex.Message}");
        }

        var psi = BuildStartInfo(config, request, logPath);

        Process? process;
        try {
            ProcessHelpers.PreventInheritedHandles();
            process = starter.Start(psi);
        } catch (Exception ex) {
            return new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, null, ex.Message);
        }
        if (process is null) return new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, null, "the process did not start");

        process.StandardInput.Close();
        process.StandardError.Close();

        return Settle(process, logPath, Readiness, time);
    }

    /// <summary>Running only on the child's accepted line; an exit is classified by its code; a child
    /// still alive and silent at <paramref name="bound"/> is <see cref="BackgroundImportStatus.Unconfirmed"/>.</summary>
    internal static BackgroundImportLaunch Settle(Process process, string logPath, TimeSpan bound, TimeProvider time) {
        var started = time.GetTimestamp();

        // A blocking read on a pipe is not reliably cancellable, so a read still pending at the bound is
        // left to finish on its own when the child writes or exits.
        var read = Task.Run(() => ReadAccepted(process.StandardOutput));
        if (read.Wait(bound) && read.Result) {
            process.StandardOutput.Close();
            return new BackgroundImportLaunch(BackgroundImportStatus.Running, logPath, null, null);
        }

        var remaining = bound - time.GetElapsedTime(started);
        if (!process.WaitForExit(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
            return new BackgroundImportLaunch(BackgroundImportStatus.Unconfirmed, logPath, null, null);

        return process.ExitCode == 0
            ? new BackgroundImportLaunch(BackgroundImportStatus.ExitedZero, logPath, 0, null)
            : new BackgroundImportLaunch(BackgroundImportStatus.Failed, logPath, process.ExitCode, $"exit {process.ExitCode}");
    }

    static bool ReadAccepted(StreamReader stdout) {
        try {
            while (stdout.ReadLine() is { } line)
                if (line == DetachedImportLog.AcceptedLine) return true;
        } catch (IOException) { }

        return false;
    }

    /// <summary>A plan request runs <c>import --yes</c> under <see cref="ImportPlan.EnvVar"/>; any other
    /// imports everything but what the request skips, unnamed unless the user agreed to naming.</summary>
    internal static ProcessStartInfo BuildStartInfo(ConfigRoot config, BackgroundImportRequest request, string logPath) {
        var psi = new ProcessStartInfo {
            FileName               = Environment.ProcessPath ?? "kcap",
            WorkingDirectory       = request.WorkingDirectory,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        string[] argv = request switch {
            { PlanPath: not null }  => ["import", "--yes"],
            { NameSessions: true }  => ["import", "--all", "--yes"],
            _                       => ["import", "--all", "--yes", "--skip-title"],
        };
        foreach (var arg in argv) psi.ArgumentList.Add(arg);
        if (request.PlanPath is null) foreach (var arg in request.Skip.ImportArgs) psi.ArgumentList.Add(arg);

        // A URL override outranks the profile pin and resolves to no profile, so it must not travel:
        // the child's capture lists and visibility come from the profile setup just saved.
        psi.Environment[ConfigRoot.ConfigDirEnvVar]        = config.Directory;
        psi.Environment[ProfileOverrides.ProfileVar]       = request.ProfileName;
        psi.Environment.Remove(ProfileOverrides.UrlVar);
        psi.Environment[DetachedImportLog.EnvVar]           = logPath;
        psi.Environment[DetachedImportLog.VisibilityEnvVar] = request.DefaultVisibility;

        if (request.PlanPath is { } plan) psi.Environment[ImportPlan.EnvVar] = plan;
        else psi.Environment.Remove(ImportPlan.EnvVar);

        return psi;
    }
}
