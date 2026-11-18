using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Services;

/// <summary>Says that closing the main window keeps kcap running in the notification area: the tray icon's tooltip
/// leads with it while the window is hidden, and a toast says it once when one can be shown. Until a toast has said
/// it, every hide repeats the tooltip, since nothing proves a tooltip was ever hovered.</summary>
public sealed class CloseToTrayHint(IAppStateStore state, Func<Task<bool>> canNotify, Action<DesktopNotification> notify) {
    public const string NotificationId = "close-to-tray";
    public const string Title = "kcap is still running";
    public const string Body = "Open it or quit from its icon in the notification area.";
    public const string TrayText = "kcap is still running: click to open it, or right-click to quit";

    bool _explained;
    bool _deciding;
    bool _hidden;

    /// <summary>The line the tray icon's tooltip leads with, or null.</summary>
    public string? TrayHint { get; private set; }

    public event Action<string?>? TrayHintChanged;

    public async Task OnHiddenAsync() {
        _hidden = true;
        // A hide that lands while an earlier one is being decided is served by that decision, which reads _hidden last.
        if (_explained || _deciding) return;
        _deciding = true;
        try {
            if ((await state.LoadAsync()).CloseToTrayExplained) {
                _explained = true;
                return;
            }
            var notifies = await canNotify();
            // A window opened again while this hide was being decided needs no explanation; the next hide gives it.
            if (!_hidden) return;

            // A toast the platform accepts can still fail to appear, so the tooltip says it whether or not one is shown.
            SetTrayHint(TrayText);
            if (!notifies) return;
            _explained = true;
            notify(new DesktopNotification(NotificationId, Title, Body, []));
            await state.UpdateAsync(s => s with { CloseToTrayExplained = true });
        } finally {
            _deciding = false;
        }
    }

    public void OnShown() {
        _hidden = false;
        SetTrayHint(null);
    }

    void SetTrayHint(string? hint) {
        if (TrayHint == hint) return;
        TrayHint = hint;
        TrayHintChanged?.Invoke(hint);
    }
}
