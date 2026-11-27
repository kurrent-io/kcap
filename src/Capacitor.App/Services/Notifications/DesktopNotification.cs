namespace Capacitor.App.Services.Notifications;

/// <param name="TimeSensitive">Asks the platform to deliver it through a Focus mode, where the app is entitled to.</param>
public sealed record DesktopNotification(
    string Id,
    string Title,
    string Body,
    IReadOnlyList<DesktopNotificationAction> Actions,
    bool TimeSensitive = false);
