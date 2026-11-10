using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>The sessions whose SessionStart ack said the server delivers pass notices at their prompts, each
/// with the server that said so. Best effort throughout: a lost marker costs the prompt-time notices, which
/// the inbox still holds, never the hook.</summary>
sealed class PassNoticeSessions(ConfigRoot config) {
    public void Arm(string sessionId, string serverUrl) {
        try {
            var marker = Marker(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, serverUrl);
        } catch { }
    }

    public void Disarm(string sessionId) {
        try { File.Delete(Marker(sessionId)); } catch { }
    }

    /// <summary>Whether the session is armed for any server; cheap enough to ask before the profile is resolved.</summary>
    public bool IsArmed(string sessionId) {
        try { return File.Exists(Marker(sessionId)); } catch { return false; }
    }

    /// <summary>A profile switched mid-session points the prompt at a server that never said it delivers there.</summary>
    public bool IsArmedFor(string sessionId, string serverUrl) {
        try { return File.Exists(Marker(sessionId)) && File.ReadAllText(Marker(sessionId)) == serverUrl; } catch { return false; }
    }

    string Marker(string sessionId) => config.Path("pass-notices", PolicySnapshotStore.Sanitize(sessionId));
}
