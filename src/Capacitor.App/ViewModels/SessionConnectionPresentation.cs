using System.Windows.Input;

namespace Capacitor.App.ViewModels;

/// One visual vocabulary for session-wide connection feedback, independent of transport.
public sealed record SessionConnectionPresentation(string Title, string Detail, bool IsBusy,
        string ActionLabel = "", ICommand? Action = null) {
    public bool HasAction => Action is not null;
    public static SessionConnectionPresentation Starting(string detail, ICommand? terminal = null) =>
        new("Starting session…", detail, true, "Open Terminal", terminal);
    public static SessionConnectionPresentation Connecting() =>
        new("Connecting to session…", "Loading your chat and work details.", true);
    public static SessionConnectionPresentation Reconnecting(string detail = "Retrying automatically. Your chat and work details are kept.") =>
        new("Reconnecting to session…", detail, true);
    public static SessionConnectionPresentation Failed(ICommand retry) =>
        new("Couldn’t reconnect to this session", "Retry the connection to continue.", false, "Retry", retry);
}
