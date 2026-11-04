using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Starting and stopping the short-lived <c>codex app-server</c> the probes ask questions of.</summary>
internal static class CodexAppServer {
    static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    /// <summary>Runs in the temp directory, under the Codex home <paramref name="directory"/>.</summary>
    public static ProcessStartInfo StartInfo(string exe, string directory, UserHome home) {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi  = new ProcessStartInfo {
            FileName               = exe,
            WorkingDirectory       = Path.GetTempPath(),
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardInputEncoding  = utf8,
            StandardOutputEncoding = utf8,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("app-server");
        VendorProcess.PlanHome(psi, HarnessId.Codex, directory, home);

        return psi;
    }

    public static async Task ShutDownAsync(Process process, TimeProvider time) {
        try {
            process.StandardInput.Close();
            using var grace = new CancellationTokenSource(ExitGrace, time);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        } catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) {
            // Still running, or its input already gone: the kill below settles it.
        }

        VendorProcess.Stop(process);
    }
}
