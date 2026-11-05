namespace Capacitor.App.Services.Voice;

/// The app as voice control drives it. Every member is called on the UI thread.
public interface IVoiceHost {
    /// Brings the main window up; a command is pointless on a window the user cannot see.
    void ShowWindow();

    /// The sessions in the rail's order.
    IReadOnlyList<VoiceSession> Sessions { get; }

    VoiceSession? OpenSession { get; }

    bool Open(string agentId);

    void GoHome();

    /// False when the open session has no such tab.
    bool ShowTab(VoiceTab tab);

    /// Sends to the open session. Null on success, else why it was not sent.
    Task<string?> SendPromptAsync(string text);

    /// Puts text in the open session's composer, or the launcher's prompt when none is open.
    bool Dictate(string text);

    /// Sends what the composer holds. Null on success, else why it was not sent.
    Task<string?> SendDraftAsync();

    /// Starts a session from the launcher's current repository and harness. Null on success.
    Task<string?> StartSessionAsync(string prompt);

    /// Starts a session at a permission level for that session only; the launcher keeps its own. Null on success.
    Task<string?> StartSessionAsync(string prompt, VoicePermissionLevel level) => StartSessionAsync(prompt);

    /// Sets the launcher's permission level for new sessions. The setting's name, or null when the
    /// chosen agent has none.
    Task<string?> SetPermissionsAsync(VoicePermissionLevel level) => Task.FromResult<string?>(null);

    /// The repository or harness label chosen, or null when nothing matched.
    Task<string?> SelectRepositoryAsync(string query);
    Task<string?> SelectHarnessAsync(string query);

    /// What the launcher would start: repository and harness, for confirmation.
    string LauncherSummary { get; }

    bool StopAgent();
    bool Interrupt();

    /// The open session's first unanswered permission request, as it would be read out.
    string? PendingPermission { get; }

    Task<bool> AnswerPermissionAsync(PermissionAnswer answer);

    /// The open session's latest reply, as Markdown.
    string? LastReply { get; }

    string StatusSummary { get; }

    void OpenSettings();
    bool Refresh();

    /// 1 larger, -1 smaller, 0 back to the default.
    void Zoom(int direction);

    /// What the session open now, or the next one to open within a minute, says next: its reply
    /// once the turn ends, and anything it stops to ask on the way.
    IObservable<VoiceNarration> WatchNextTurn();
}
