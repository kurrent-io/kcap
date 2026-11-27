namespace Capacitor.App.Services.Notifications;

public interface IDesktopNotificationAccess {
    Task<DesktopNotificationAccess> GetAsync();

    async Task<bool> NeedsRequestAsync() => await GetAsync() == DesktopNotificationAccess.NotDetermined;

    /// Requests the platform's notification interactions, including additional supported types.
    /// An undetermined request can prompt, so call this while the user is looking at the app.
    Task<DesktopNotificationAccess> RequestAsync();

    /// Whether time-sensitive notices come through a Focus mode; null where the platform has no Focus to break through.
    Task<bool?> TimeSensitiveAsync() => Task.FromResult<bool?>(null);

    void OpenSystemSettings();
}
