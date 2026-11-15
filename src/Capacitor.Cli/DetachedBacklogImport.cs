using System.Diagnostics;
using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Hands a session's transcript to a detached <c>kcap import</c> when a session-end hook's pre-drain cap cuts the upload
/// off. The hook has to exit inside the host's timeout, and nothing else finishes a backlog once the session has ended;
/// the import sends the whole transcript and the server keeps only what it has not taken.
/// </summary>
public static class DetachedBacklogImport {
    public static bool Spawn(string vendorFlag, string sessionId, ConfigRoot config, IProcessStarter starter) {
        try {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? "kcap") {
                UseShellExecute = false,
                CreateNoWindow  = true,
            };

            foreach (var arg in Args(vendorFlag, sessionId)) psi.ArgumentList.Add(arg);
            psi.Environment[ConfigRoot.ConfigDirEnvVar] = config.Directory;

            return starter.StartDetached(psi) is not null;
        } catch (Exception) {
            return false;
        }
    }

    public static string[] Args(string vendorFlag, string sessionId) =>
        ["import", vendorFlag, "--session", sessionId, "--skip-title", "--no-update-check"];
}
