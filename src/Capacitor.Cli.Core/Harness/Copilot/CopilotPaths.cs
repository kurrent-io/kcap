namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>
/// Filesystem layout for GitHub Copilot CLI state. Everything lives under a
/// single root: <c>$COPILOT_HOME</c> when set (Copilot relocates its entire
/// tree through that variable — hooks inherit it from the spawning process),
/// otherwise <c>~/.copilot</c> on every OS.
/// </summary>
public sealed class CopilotPaths {
    /// <param name="xdgConfigHome">Where VS Code keeps its user data on Linux when set.</param>
    public CopilotPaths(UserHome home, string? xdgConfigHome, string? copilotHome) {
        Root = !string.IsNullOrEmpty(copilotHome) ? copilotHome : Path.Combine(home.Path, ".copilot");

#pragma warning disable RS0030 // Roaming AppData is not derivable from a UserHome
        var appData = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : "";
#pragma warning restore RS0030

        // An AppData the OS declines to name would leave the paths relative, read against the working directory.
        string? userData = OperatingSystem.IsMacOS() ? Path.Combine(home.Path, "Library", "Application Support")
                         : OperatingSystem.IsWindows() ? (appData.Length > 0 ? appData : null)
                         : !string.IsNullOrEmpty(xdgConfigHome) ? xdgConfigHome
                         : Path.Combine(home.Path, ".config");

        VsCodeWorkspaceStorageDirs = userData is null
            ? []
            : VsCodeEditions.Select(edition => Path.Combine(userData, edition, "User", "workspaceStorage")).ToArray();
    }

    static readonly string[] VsCodeEditions = ["Code", "Code - Insiders"];

    public string Root { get; }

    /// <summary>
    /// User-level hooks directory. Copilot merges every <c>*.json</c> file in
    /// here at startup, so kcap owns its own file (<see cref="KcapHooksJson"/>)
    /// instead of merging into a shared one the way the Cursor installer must.
    /// </summary>
    public string HooksDir => Path.Combine(Root, "hooks");

    public string KcapHooksJson => Path.Combine(HooksDir, "kcap.json");

    /// <summary>
    /// User-level MCP server config (<c>mcpServers</c> object, each entry
    /// <c>type: "stdio"</c>). Copilot reads it from the root dir, so it honors
    /// <c>$COPILOT_HOME</c> like the rest of the layout.
    /// </summary>
    public string McpConfigJson => Path.Combine(Root, "mcp-config.json");

    /// <summary>
    /// User-level custom-instructions file Copilot loads into context at startup. Honors
    /// <c>$COPILOT_HOME</c> like the rest of the layout.
    /// </summary>
    public string InstructionsMd => Path.Combine(Root, "copilot-instructions.md");

    /// <summary>
    /// Per-session state root: one subdirectory per session (named with the
    /// dashed session uuid) containing <c>events.jsonl</c> (append-only
    /// transcript), <c>workspace.yaml</c> (cwd/repo/title metadata), and
    /// checkpoint artifacts. Directories WITHOUT an events.jsonl are
    /// failed-startup scaffolding and must be skipped by discovery.
    /// </summary>
    public string SessionStateDir => Path.Combine(Root, "session-state");

    /// <summary>
    /// Pre-GA session storage (Copilot migrated to <c>session-state/</c> in
    /// late 2025; old sessions are only migrated lazily on resume). Import
    /// walks both roots.
    /// </summary>
    public string LegacySessionStateDir => Path.Combine(Root, "history-session-state");

    /// <summary>Both session-state roots, current first: a session migrated on resume exists in
    /// both, and <c>session-state/</c> holds the longer transcript.</summary>
    public IReadOnlyList<string> SessionStateDirs => [SessionStateDir, LegacySessionStateDir];

    /// <summary>Transcript path for a session dir name (the dashed session uuid) under whichever of
    /// <see cref="SessionStateDirs"/> the caller is walking.</summary>
    public string EventsJsonl(string sessionStateDir, string sessionDirName)
        => Path.Combine(sessionStateDir, sessionDirName, "events.jsonl");

    /// <summary>Whether a transcript is the one VS Code's Local harness writes
    /// (<c>GitHub.copilot-chat/transcripts/{id}.jsonl</c>), rather than Copilot's own
    /// <c>{id}/events.jsonl</c>. Local writes no <c>session.shutdown</c>, fires no session-end hook and
    /// has no agent process above its hooks, so its end can only be read from the file going quiet.</summary>
    public static bool IsVsCodeLocalTranscript(string transcriptPath)
        => !string.Equals(Path.GetFileName(transcriptPath), "events.jsonl", StringComparison.OrdinalIgnoreCase);

    /// <summary>VS Code's per-workspace storage roots on this host, stable then Insiders. Each workspace's
    /// <c>GitHub.copilot-chat/transcripts/</c> holds its Local sessions' transcripts, <c>chatSessions/</c> the chat logs
    /// with their usage and titles, and <c>workspace.json</c> the folder it is open on.</summary>
    public IReadOnlyList<string> VsCodeWorkspaceStorageDirs { get; }

    /// <summary>The chat log VS Code keeps beside a Local transcript, <c>{workspace}/chatSessions/{id}.jsonl</c>, or
    /// null for a transcript not in that layout.</summary>
    public static string? ChatSessionLogFor(string transcriptPath) {
        var transcripts = Path.GetDirectoryName(transcriptPath);
        var extension   = Path.GetDirectoryName(transcripts);
        var workspace   = Path.GetDirectoryName(extension);

        if (workspace is null
         || !string.Equals(Path.GetFileName(transcripts), "transcripts", StringComparison.OrdinalIgnoreCase)
         || !string.Equals(Path.GetFileName(extension), "GitHub.copilot-chat", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        return Path.Combine(workspace, "chatSessions", Path.GetFileName(transcriptPath));
    }

    public string WorkspaceYaml(string sessionStateDir, string sessionDirName)
        => Path.Combine(sessionStateDir, sessionDirName, "workspace.yaml");

    /// <summary>Whether Copilot has run here, by what it writes itself. Wiring creates this root and its
    /// hooks, MCP config and instructions, so none of those can say so; VS Code writes <c>logs</c> on its
    /// first launch, before any sign-in.</summary>
    public bool HasUserData() =>
        CopilotOwnDirectories.Any(dir => Directory.Exists(Path.Combine(Root, dir)))
     || File.Exists(Path.Combine(Root, "config.json"));

    static readonly string[] CopilotOwnDirectories = ["logs", "ide", "session-state", "history-session-state"];

    /// <summary>Whether VS Code has signed Copilot in here: only its harness writes <c>ide</c>.</summary>
    public bool HasVsCodeSignIn() => Directory.Exists(Path.Combine(Root, "ide"));
}
