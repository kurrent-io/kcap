using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Services;

/// <summary>Says once per user that closing the main window keeps kcap running in the notification area: a
/// desktop notification, or the tray icon's tooltip until the window opens again when notifications cannot be
/// shown. Without it a close looks like a quit that did not happen.</summary>
public sealed class CloseToTrayHint(IAppStateStore state, Func<Task<bool>> canNotify, Action<DesktopNotification> notify) {
    public const string NotificationId = "close-to-tray";
    public const string Title = "kcap is still running";
    public const string Body = "Open it or quit from its icon in the notification area.";
    public const string TrayText = "kcap is still running: click to open it, or right-click to quit";

    bool _handled;

    /// <summary>The line the tray icon's tooltip leads with, or null.</summary>
    public string? TrayHint { get; private set; }

    public event Action<string?>? TrayHintChanged;

    public async Task OnHiddenAsync() {
        if (_handled) return;
        _handled = true;

        if ((await state.LoadAsync()).CloseToTrayExplained) return;
        await state.UpdateAsync(s => s with { CloseToTrayExplained = true });

        if (await canNotify()) notify(new DesktopNotification(NotificationId, Title, Body, []));
        else SetTrayHint(TrayText);
    }

    public void OnShown() => SetTrayHint(null);

    void SetTrayHint(string? hint) {
        if (TrayHint == hint) return;
        TrayHint = hint;
        TrayHintChanged?.Invoke(hint);
    }
}
