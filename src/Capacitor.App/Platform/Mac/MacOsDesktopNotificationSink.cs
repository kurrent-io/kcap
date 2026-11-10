using System.Reactive.Subjects;
using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Platform.Mac;

internal sealed class MacOsDesktopNotificationSink : IDesktopNotificationSink, IDesktopNotificationAccess, IDesktopNotificationBadge {
    sealed record Pending(DesktopNotification Source, string NativeId, string Category, Action<string?> Activated) {
        public DateTimeOffset? Accepted { get; set; }
        public bool Observed { get; set; }
        public bool Published { get; set; }
    }

    readonly IMacNotificationCenter _center;
    readonly Action<Action> _dispatch;
    readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    readonly Dictionary<string, (Pending Pending, DateTimeOffset Expires)> _activationResponses = new(StringComparer.Ordinal);
    readonly BehaviorSubject<int> _counts = new(0);
    readonly MacNotificationBadge _badge;
    readonly TimeProvider _time;
    readonly ITimer _reconcileTimer;
    bool _disposed, _reconciling;
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan DeliveryGrace = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ResponseGrace = TimeSpan.FromSeconds(30);

    internal MacOsDesktopNotificationSink(IMacNotificationCenter center, Action<Action> dispatch, TimeProvider? time = null) {
        _center = center;
        _dispatch = dispatch;
        _time = time ?? TimeProvider.System;
        _reconcileTimer = _time.CreateTimer(_ => Post(() => { _ = ReconcileAsync(); }), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _badge = new((count, completed) => center.SetBadge(count, success => Post(() => completed(success))),
            count => { if (!_disposed) _counts.OnNext(count); });
        center.Responded += OnResponded;
        center.Suppressed += OnSuppressed;
        _badge.Authorize(false);
    }

    public IObservable<int> BadgeCounts => _counts;

    public void Show(DesktopNotification notification, Action<string?> activated) {
        if (_disposed || !_center.Available) return;
        Close(notification.Id);
        foreach (var key in _activationResponses.Where(p => p.Value.Pending.Source.Id == notification.Id).Select(p => p.Key).ToArray())
            _activationResponses.Remove(key);
        try {
            var category = _center.AcquireCategory(notification.Actions);
            var pending = new Pending(notification, Guid.NewGuid().ToString("N"), category, activated);
            _pending.Add(notification.Id, pending);
            _reconcileTimer.Change(ReconcileInterval, ReconcileInterval);
            _ = AuthorizeAndPublishAsync(pending);
        } catch (Exception error) {
            Close(notification.Id);
            DesktopNotifications.Report(error);
        }
    }

    async Task AuthorizeAndPublishAsync(Pending pending) {
        try {
            await _center.RequestAsync();
            var settings = await _center.SettingsAsync();
            Post(() => {
                if (!IsCurrent(pending)) return;
                _badge.Authorize(settings.Badge);
                if (settings.Access != DesktopNotificationAccess.Allowed) { Close(pending.Source.Id); return; }
                pending.Published = true;
                try {
                    _center.Publish(pending.Source, pending.NativeId, pending.Category, settings.Badge ? _badge.Count + 1 : null,
                        failure => Post(() => {
                            // An asynchronous add may finish after withdrawal or app activation.
                            if (!IsCurrent(pending)) { _center.Withdraw(pending.NativeId); _badge.Refresh(); return; }
                            if (failure is null) {
                                pending.Accepted = _time.GetUtcNow();
                                _badge.Delivered(pending.NativeId);
                            }
                            else { Close(pending.Source.Id); DesktopNotifications.Report(failure); }
                        }));
                } catch (Exception error) {
                    Close(pending.Source.Id);
                    DesktopNotifications.Report(error);
                }
            });
        } catch (Exception error) {
            Post(() => { if (IsCurrent(pending)) Close(pending.Source.Id); });
            DesktopNotifications.Report(error);
        }
    }

    public async Task<DesktopNotificationAccess> GetAsync() {
        if (_disposed || !_center.Available) return DesktopNotificationAccess.Unknown;
        var settings = await _center.SettingsAsync();
        Post(() => { if (!_disposed) _badge.Authorize(settings.Badge); });
        return settings.Access;
    }

    public async Task<bool> NeedsRequestAsync() {
        if (_disposed || !_center.Available) return false;
        var settings = await _center.SettingsAsync();
        Post(() => { if (!_disposed) _badge.Authorize(settings.Badge); });
        return settings.Access == DesktopNotificationAccess.NotDetermined
            || settings.Access == DesktopNotificationAccess.Allowed && !settings.Badge;
    }

    public async Task<DesktopNotificationAccess> RequestAsync() {
        if (_disposed || !_center.Available) return DesktopNotificationAccess.Unknown;
        await _center.RequestAsync();
        return await GetAsync();
    }

    public void OpenSystemSettings() => _center.OpenSystemSettings();

    internal static DesktopNotificationAccess Access(nint authorizationStatus) => authorizationStatus switch {
        0 => DesktopNotificationAccess.NotDetermined,
        1 => DesktopNotificationAccess.Denied,
        2 or 3 or 4 => DesktopNotificationAccess.Allowed,
        _ => DesktopNotificationAccess.Unknown,
    };

    bool IsCurrent(Pending pending) => !_disposed && _pending.TryGetValue(pending.Source.Id, out var current) && ReferenceEquals(current, pending);

    void OnSuppressed(string nativeId) => Post(() => {
        var pending = _pending.Values.FirstOrDefault(p => p.NativeId == nativeId);
        if (pending is not null) Close(pending.Source.Id);
    });

    void OnResponded(string nativeId, string? nativeAction) => Post(() => {
        var pending = _pending.Values.FirstOrDefault(p => p.NativeId == nativeId);
        if (pending is null && _activationResponses.Remove(nativeId, out var recent) && recent.Expires >= _time.GetUtcNow())
            pending = recent.Pending;
        if (_disposed || pending is null) return;
        if (nativeAction == "com.apple.UNNotificationDismissActionIdentifier") {
            if (IsCurrent(pending)) Close(pending.Source.Id);
            return;
        }
        string? action = null;
        if (nativeAction != "com.apple.UNNotificationDefaultActionIdentifier") {
            action = pending.Source.Actions.FirstOrDefault(a => pending.Category + "." + a.Id == nativeAction)?.Id;
            if (action is null) return;
        }
        if (IsCurrent(pending)) Close(pending.Source.Id);
        pending.Activated(action);
    });

    public void OnActivated() {
        // App activation can precede the response that opened the app.
        foreach (var pending in _pending.Values.Where(p => p.Published))
            _activationResponses[pending.NativeId] = (pending, _time.GetUtcNow() + ResponseGrace);
        foreach (var id in _pending.Keys.ToArray()) Close(id);
    }

    internal async Task ReconcileAsync() {
        if (_disposed || _reconciling) return;
        foreach (var key in _activationResponses.Where(p => p.Value.Expires < _time.GetUtcNow()).Select(p => p.Key).ToArray())
            _activationResponses.Remove(key);
        StopIdleTimer();
        var snapshot = _pending.Values.Where(p => p.Accepted is not null).ToArray();
        if (snapshot.Length == 0) return;
        _reconciling = true;
        try {
            var settings = await _center.SettingsAsync().WaitAsync(TimeSpan.FromSeconds(5), _time);
            var delivered = await _center.DeliveredAsync().WaitAsync(TimeSpan.FromSeconds(5), _time);
            Post(() => {
                if (_disposed) return;
                _badge.Authorize(settings.Badge && delivered is not null);
                if (delivered is null) return;
                foreach (var pending in snapshot.Where(IsCurrent)) {
                    if (delivered.Contains(pending.NativeId)) pending.Observed = true;
                    // Delivery can follow the asynchronous add's completion.
                    else if (pending.Observed || _time.GetUtcNow() - pending.Accepted >= DeliveryGrace) Close(pending.Source.Id);
                }
                if (settings.Badge) _badge.Refresh();
            });
        } catch (Exception error) {
            Post(() => { if (!_disposed) _badge.Authorize(false); });
            DesktopNotifications.Report(error);
        } finally {
            Post(() => _reconciling = false);
        }
    }

    public void Close(string id) {
        if (!_pending.Remove(id, out var pending)) return;
        StopIdleTimer();
        _badge.Remove(pending.NativeId);
        try { _center.Withdraw(pending.NativeId); } catch (Exception error) { DesktopNotifications.Report(error); }
        try { _center.ReleaseCategory(pending.Category); } catch (Exception error) { DesktopNotifications.Report(error); }
    }

    void StopIdleTimer() {
        if (_pending.Count == 0 && _activationResponses.Count == 0)
            _reconcileTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() {
        if (_disposed) return;
        OnActivated();
        _disposed = true;
        _activationResponses.Clear();
        _reconcileTimer.Dispose();
        _center.Responded -= OnResponded;
        _center.Suppressed -= OnSuppressed;
        _center.Dispose();
        _counts.Dispose();
    }

    void Post(Action action) {
        try { _dispatch(() => { try { action(); } catch (Exception error) { DesktopNotifications.Report(error); } }); }
        catch (Exception error) { DesktopNotifications.Report(error); }
    }
}
