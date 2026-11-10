using Capacitor.App.Platform.Mac;
using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Tests.Unit;

internal sealed class FakeMacNotificationCenter : IMacNotificationCenter {
    public bool Available { get; set; } = true;
    public (DesktopNotificationAccess Access, bool Badge) Settings { get; set; } = (DesktopNotificationAccess.Allowed, true);
    public Func<Task>? Request { get; set; }
    public Func<Task<IReadOnlySet<string>?>>? ReadDelivered { get; set; }
    public int Requests { get; private set; }
    public int DeliveredReads { get; private set; }
    public bool CompleteAdds { get; set; } = true;
    public Exception? PublishFailure { get; set; }
    public Exception? WithdrawFailure { get; set; }
    public bool CompleteBadges { get; set; } = true;
    public Queue<bool> BadgeResults { get; } = new();
    public List<(DesktopNotification Source, string NativeId, string Category, int? Badge, Action<string?> Complete)> Published { get; } = [];
    public List<(int Count, Action<bool> Complete)> Badges { get; } = [];
    public List<string> Withdrawn { get; } = [];
    public List<string> Released { get; } = [];
    public event Action<string, string?>? Responded;
    public event Action<string>? Suppressed;

    public Task<(DesktopNotificationAccess Access, bool Badge)> SettingsAsync() => Task.FromResult(Settings);
    public Task<IReadOnlySet<string>?> DeliveredAsync() {
        DeliveredReads++;
        return ReadDelivered?.Invoke()
            ?? Task.FromResult<IReadOnlySet<string>?>(Published.Select(p => p.NativeId).Except(Withdrawn).ToHashSet(StringComparer.Ordinal));
    }
    public Task RequestAsync() { Requests++; return Request?.Invoke() ?? Task.CompletedTask; }
    public string AcquireCategory(IReadOnlyList<DesktopNotificationAction> actions) => "category";
    public void ReleaseCategory(string category) => Released.Add(category);
    public void Publish(DesktopNotification source, string nativeId, string category, int? badge, Action<string?> completed) {
        if (PublishFailure is { } error) throw error;
        Published.Add((source, nativeId, category, badge, completed));
        if (CompleteAdds) completed(null);
    }
    public void Withdraw(string nativeId) {
        if (WithdrawFailure is { } error) throw error;
        Withdrawn.Add(nativeId);
    }
    public void SetBadge(int count, Action<bool> completed) {
        Badges.Add((count, completed));
        if (CompleteBadges) completed(BadgeResults.Count == 0 || BadgeResults.Dequeue());
    }
    public void Respond(string nativeId, string action) => Responded?.Invoke(nativeId, action);
    public void Suppress(string nativeId) => Suppressed?.Invoke(nativeId);
    public void OpenSystemSettings() { }
    public void Dispose() { }
}
