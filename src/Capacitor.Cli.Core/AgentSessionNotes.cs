using System.Globalization;

namespace Capacitor.Cli.Core;

/// <summary>
/// The notes a hook leaves of which coding-agent process runs which session: one file per pid under the config's
/// <c>agent-sessions</c> folder, holding the session and the process's start token. A recorder reads them to tell a
/// session resumed in another process from one that has ended.
/// </summary>
public sealed class AgentSessionNotes(ConfigRoot config) {
    /// <summary>
    /// A process running <paramref name="session"/> that is not provably gone, or null. Hooks may write the id in
    /// another case than the one asked about, so the match ignores case.
    /// </summary>
    /// <param name="holderIsGone">Whether the process a note names is provably gone, given its pid and token.</param>
    public ProcessIncarnation? LiveClaimant(SessionId session, Func<int, string, bool> holderIsGone) {
        foreach (var pid in Claimants()) {
            try {
                if (File.ReadAllText(Note(pid)).Split('\n') is [var named, var token]
                 && string.Equals(SessionId.Parse(named)?.Value, session.Value, StringComparison.OrdinalIgnoreCase)
                 && !holderIsGone(pid, token))
                    return new ProcessIncarnation(pid, token);
            } catch { }
        }

        return null;
    }

    public int[] Claimants() {
        try {
            return [
                ..Directory.EnumerateFiles(config.Path("agent-sessions"))
                    .Select(file => int.TryParse(Path.GetFileName(file), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : 0)
                    .Where(pid => pid > 0)
            ];
        } catch {
            return [];
        }
    }

    public string Note(int pid) => config.Path("agent-sessions", pid.ToString(CultureInfo.InvariantCulture));
}
