using System.Diagnostics;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli;

/// <summary>
/// Hands a session's transcript to a detached <c>kcap import</c> when a session-end hook's pre-drain cap cuts the upload
/// off. The hook has to exit inside the host's timeout, and nothing else finishes a backlog once the session has ended;
/// the import sends the whole transcript and the server keeps only what it has not taken.
/// </summary>
public static class DetachedBacklogImport {
    /// <param name="transcriptPath">Where the hook read the session from: an import that would look elsewhere is not
    /// started, since it would find nothing and the hook would claim a hand-off that never happens.</param>
    /// <param name="importSearches">The directory the vendor's import discovers sessions in, when a session can live
    /// outside it; null when the hook and the import read the same place.</param>
    /// <param name="resolution">How the hook resolved its server. The import inherits the hook's environment and directory,
    /// so it resolves the same way unless the hook was given <c>--server-url</c>, which it then gets too: a URL pinned any
    /// other way would also drop the profile whose exclusions the hook applied.</param>
    public static bool Spawn(
            string vendorFlag, string sessionId, string transcriptPath, string? importSearches, ConfigRoot config,
            ResolvedProfile resolution, IProcessStarter starter) {
        if (importSearches is not null && !Under(transcriptPath, importSearches)) return false;
        if (DisabledSessions.IsDisabledByPerson(SessionIds.Canonical(sessionId) ?? sessionId, config)) return false;

        try {
            // Every standard stream redirected, and closed on this side by the starter: inherited, they would hold the
            // host's hook pipes open past the hook's exit and put the import's output in the hook's answer.
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? "kcap") {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            foreach (var arg in Args(vendorFlag, sessionId)) psi.ArgumentList.Add(arg);
            if (resolution is { Source: UrlSource.CommandLine, ServerUrl: { } cliUrl }) {
                psi.ArgumentList.Add("--server-url");
                psi.ArgumentList.Add(cliUrl);
            }
            psi.Environment[ConfigRoot.ConfigDirEnvVar] = config.Directory;
            // A detached import: no parent watchdog, which would end it moments after the hook exits, and its output in a
            // log, which the child only appends to.
            var log = LogPath(config, sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, "");
            psi.Environment[DetachedImportLog.EnvVar] = log;
            psi.Environment.Remove(DetachedImportLog.VisibilityEnvVar);

            return starter.StartDetached(psi) is not null;
        } catch (Exception) {
            return false;
        }
    }

    public static string[] Args(string vendorFlag, string sessionId) =>
        // No minimum length: what is left of a cut-off backlog is worth sending however short the session is.
        ["import", vendorFlag, "--session", sessionId, "--min-lines", "0", "--skip-title", "--no-update-check"];

    public static string LogPath(ConfigRoot config, string sessionId) =>
        config.Path("logs", $"backlog-import-{SessionIds.Canonical(sessionId) ?? sessionId}.log");

    static bool Under(string path, string directory) {
        var dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return Path.GetFullPath(path).StartsWith(dir, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }
}
