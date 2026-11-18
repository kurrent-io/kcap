using System.Windows.Input;

namespace Capacitor.App.ViewModels;

/// One visual vocabulary for session-wide connection feedback, independent of transport. A notice that
/// names a cause offers the action that repairs it; a plain Retry only re-runs a connection attempt.
public sealed record SessionConnectionPresentation(string Title, string Detail, bool IsBusy,
        string ActionLabel = "", ICommand? Action = null, bool IsPlain = false) {
    /// The action fixes the named cause, so it is the primary button; any other action is secondary.
    public bool RepairsCause { get; init; }
    public bool HasDetail => Detail.Length > 0;
    public bool HasAction => Action is not null;
    public bool ShowsAlert => !IsBusy && !IsPlain;
    public static SessionConnectionPresentation Starting(string detail, ICommand? terminal = null) =>
        new("Starting session…", detail, true, "Open Terminal", terminal);
    public static SessionConnectionPresentation Connecting() =>
        new("Connecting to session…", "Loading your chat and work details.", true);
    public static SessionConnectionPresentation Reconnecting(string detail = "Retrying automatically. Your chat and work details are kept.") =>
        new("Reconnecting to session…", detail, true);
    public static SessionConnectionPresentation Failed(ICommand retry) =>
        new("Couldn’t reconnect to this session", "Retry the connection to continue.", false, "Retry", retry);
    /// <param name="failure">Why the last start did not take, so a second press is not a blind guess.</param>
    public static SessionConnectionPresentation DaemonStopped(ICommand start, string? failure = null) =>
        failure is null
            ? new("The daemon isn’t running", "Start it to reconnect this session.", false, "Start daemon", start) { RepairsCause = true }
            : new("The daemon didn’t start", failure, false, "Try again", start) { RepairsCause = true };
    public static SessionConnectionPresentation DaemonStarting() =>
        new("Starting the daemon…", "This session reconnects when it’s ready.", true);
    public static SessionConnectionPresentation LocalServerStopped(ICommand start) =>
        new("Your kcap server is stopped", "Start it to reconnect this session.", false, "Start server", start) { RepairsCause = true };
    public static SessionConnectionPresentation LocalServerStarting() =>
        new("Starting your kcap server…", "This session reconnects when it’s ready.", true);
}
