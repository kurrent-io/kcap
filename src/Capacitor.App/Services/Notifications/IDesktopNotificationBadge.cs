namespace Capacitor.App.Services.Notifications;

public interface IDesktopNotificationBadge {
    IObservable<int> BadgeCounts { get; }
    void OnActivated();
}
