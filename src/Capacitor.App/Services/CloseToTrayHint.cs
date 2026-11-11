using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Services;

/// <summary>Says once per user that closing the main window keeps kcap running in the notification area: the tray
/// icon's tooltip leads with it until the window opens again, and a desktop notification says it too when
/// notifications are allowed. Without it a close looks like a quit that did not happen.</summary>
public sealed class CloseToTrayHint(IAppStateStore state, Func<Task<bool>> canNotify, Action<DesktopNotification> notify) {
    public const string NotificationId = "close-to-tray";
    public const string Title = "kcap is still running";
    public const string Body = "Open it or quit from its icon in the notification area.";
    public const string TrayText = "kcap is still running: click to open it, or right-click to quit";

    bool _handled;
    int _shows;

    /// <summary>The line the tray icon's tooltip leads with, or null.</summary>
    public string? TrayHint { get; private set; }

    public event Action<string?>? TrayHintChanged;

    public async Task OnHiddenAsync() {
        if (_handled) return;
        _handled = true;
        var shows = _shows;

        if ((await state.LoadAsync()).CloseToTrayExplained) return;
        var notifies = await canNotify();
        // A window opened again while this hide was being decided needs no explanation; the next hide gives it.
        if (shows != _shows) {
            _handled = false;
            return;
        }

        // A toast the platform accepts can still fail to appear, so the tooltip says it whether or not one is shown.
        SetTrayHint(TrayText);
        if (notifies) notify(new DesktopNotification(NotificationId, Title, Body, []));
        await state.UpdateAsync(s => s with { CloseToTrayExplained = true });
    }

    public void OnShown() {
        _shows++;
        SetTrayHint(null);
    }

    void SetTrayHint(string? hint) {
        if (TrayHint == hint) return;
        TrayHint = hint;
        TrayHintChanged?.Invoke(hint);
    }
}
