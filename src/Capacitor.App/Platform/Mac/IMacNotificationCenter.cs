using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Platform.Mac;

internal interface IMacNotificationCenter : IDisposable {
    bool Available { get; }
    event Action<string, string?>? Responded;
    event Action<string>? Suppressed;
    Task<(DesktopNotificationAccess Access, bool Badge)> SettingsAsync();
    Task<IReadOnlySet<string>?> DeliveredAsync();
    Task RequestAsync();
    string AcquireCategory(IReadOnlyList<DesktopNotificationAction> actions);
    void ReleaseCategory(string category);
    void Publish(DesktopNotification source, string nativeId, string category, int? badge, Action<string?> completed);
    void Withdraw(string nativeId);
    void SetBadge(int count, Action<bool> completed);
    void OpenSystemSettings();
}
