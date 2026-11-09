using System.Diagnostics;
using System.Text;

namespace Capacitor.Cli.Core.Workspace;

/// <summary>Runs git for a workspace snapshot under a deadline, keeping at most a fixed amount of stdout.</summary>
public sealed class WorkspaceGit(TimeProvider time, int outputLimit = WorkspaceGit.DefaultOutputLimit) {
    public const int DefaultOutputLimit = 8 * 1024 * 1024;

    public async Task<WorkspaceGitResult> RunAsync(string dir, TimeSpan timeout, params string[] args) {
        if (timeout <= TimeSpan.Zero) return new WorkspaceGitResult(-1, "", "", Cut: false, TimedOut: true);

        var psi = new ProcessStartInfo("git") {
            WorkingDirectory       = dir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        // A snapshot must never take the index lock an agent's own git command is waiting on.
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotePath=false");
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        Process process;
        try {
            process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
            return new WorkspaceGitResult(-1, "", ex.Message, Cut: false, TimedOut: false);
        }

        using (process) {
            process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(timeout, time);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var stdout = new StringBuilder();
            var cut    = false;
            var buffer = new char[16 * 1024];

            try {
                while (true) {
                    var read = await process.StandardOutput.ReadAsync(buffer, deadline.Token);
                    if (read == 0) break;
                    if (stdout.Length + read > outputLimit) {
                        stdout.Append(buffer, 0, outputLimit - stdout.Length);
                        cut = true;
                        break;
                    }
                    stdout.Append(buffer, 0, read);
                }

                if (cut) Kill(process);
                await process.WaitForExitAsync(deadline.Token);
            } catch (OperationCanceledException) when (deadline.IsCancellationRequested) {
                Kill(process);
                return new WorkspaceGitResult(-1, stdout.ToString(), "", cut, TimedOut: true);
            }

            var err = "";
            try { err = await stderr; } catch (IOException) { }

            return new WorkspaceGitResult(cut ? 0 : process.ExitCode, stdout.ToString(), err, cut, TimedOut: false);
        }
    }

    static void Kill(Process process) {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
}
