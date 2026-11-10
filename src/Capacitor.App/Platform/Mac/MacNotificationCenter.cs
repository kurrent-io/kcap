using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Capacitor.App.Platform.Mac.MacNotificationInterop;

using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Platform.Mac;

/// UserNotifications requires a real application bundle. A development `dotnet run` intentionally
/// has no native sink; calling currentNotificationCenter from an unbundled process can abort it.
[SupportedOSPlatform("macos")]
internal sealed class MacNotificationCenter : IMacNotificationCenter {
    static readonly ConcurrentDictionary<nint, WeakReference<MacNotificationCenter>> Delegates = new();
    internal const nint AuthorizationOptions = 5; // UNAuthorizationOptionAlert | UNAuthorizationOptionBadge
    static nint _delegateClass;
    readonly MacNotificationCategories _categories;
    readonly nint _center;
    readonly nint _delegate;
    readonly string? _bundleId;
    bool _disposed;

    public bool Available => !_disposed && _center != 0 && _delegate != 0;
    public event Action<string, string?>? Responded;
    public event Action<string>? Suppressed;

    public MacNotificationCenter() {
        _categories = new(CreateCategory, RegisterCategories, Release);
        if (!OperatingSystem.IsMacOSVersionAtLeast(11)) return;
        try {
            using var pool = new Pool();
            var bundle = Send(GetClass("NSBundle"), Selector("mainBundle"));
            var identifier = Text(Send(bundle, Selector("bundleIdentifier")));
            var path = Text(Send(bundle, Selector("bundlePath")));
            if (string.IsNullOrEmpty(identifier) || path?.EndsWith(".app", StringComparison.OrdinalIgnoreCase) != true) return;

            _center = Send(GetClass("UNUserNotificationCenter"), Selector("currentNotificationCenter"));
            if (_center == 0) return;
            _bundleId = identifier;
            _delegate = Send(DelegateClass(), Selector("new"));
            if (_delegate == 0) throw new InvalidOperationException("Unable to initialize the notification delegate.");
            Delegates[_delegate] = new(this);
            SendVoid(_center, Selector("setDelegate:"), _delegate);
            // Nothing from a previous process can still answer a live request.
            SendVoid(_center, Selector("removeAllPendingNotificationRequests"));
            SendVoid(_center, Selector("removeAllDeliveredNotifications"));
        } catch (Exception error) { DesktopNotifications.Report(error); }
    }

    public Task<(DesktopNotificationAccess Access, bool Badge)> SettingsAsync() {
        if (_disposed || _center == 0) return Task.FromResult((DesktopNotificationAccess.Unknown, false));
        var result = new TaskCompletionSource<(DesktopNotificationAccess Access, bool Badge)>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            using var pool = new Pool();
            // The settings object is only ours for the duration of the callback: read it there.
            using var block = MacNotificationBlock.Completion(settings => {
                try {
                    var access = settings == 0 ? DesktopNotificationAccess.Unknown : MacOsDesktopNotificationSink.Access(Send(settings, Selector("authorizationStatus")));
                    result.TrySetResult((access, settings != 0 && access == DesktopNotificationAccess.Allowed
                        && Send(settings, Selector("badgeSetting")) == 2 && OperatingSystem.IsMacOSVersionAtLeast(13)));
                } catch (Exception error) {
                    // The block swallows what escapes it, which would leave the caller waiting forever.
                    DesktopNotifications.Report(error);
                    result.TrySetResult((DesktopNotificationAccess.Unknown, false));
                }
            });
            SendVoid(_center, Selector("getNotificationSettingsWithCompletionHandler:"), block.Handle);
        } catch (Exception error) {
            DesktopNotifications.Report(error);
            result.TrySetResult((DesktopNotificationAccess.Unknown, false));
        }
        return result.Task;
    }

    public async Task RequestAsync() {
        if (_disposed || _center == 0) return;
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            using var pool = new Pool();
            using var block = MacNotificationBlock.Authorization((_, error) => {
                if (Error(error) is { } failure) answered.TrySetException(new InvalidOperationException(failure));
                else answered.TrySetResult();
            });
            SendVoid(_center, Selector("requestAuthorizationWithOptions:completionHandler:"), AuthorizationOptions, block.Handle);
        } catch (Exception error) {
            DesktopNotifications.Report(error);
            return;
        }
        await answered.Task;
    }

    public Task<IReadOnlySet<string>?> DeliveredAsync() {
        if (_disposed || _center == 0) return Task.FromResult<IReadOnlySet<string>?>(null);
        var result = new TaskCompletionSource<IReadOnlySet<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            using var pool = new Pool();
            using var block = MacNotificationBlock.Completion(notifications => {
                try {
                    if (notifications == 0) { result.TrySetResult(null); return; }
                    var identifiers = new HashSet<string>(StringComparer.Ordinal);
                    for (nint i = 0; i < Send(notifications, Selector("count")); i++) {
                        var notification = Send(notifications, Selector("objectAtIndex:"), i);
                        var request = Send(notification, Selector("request"));
                        if (Text(Send(request, Selector("identifier"))) is { } id) identifiers.Add(id);
                    }
                    result.TrySetResult(identifiers.Count == 0 ? FrozenSet<string>.Empty : identifiers);
                } catch (Exception error) {
                    DesktopNotifications.Report(error);
                    result.TrySetResult(null);
                }
            });
            SendVoid(_center, Selector("getDeliveredNotificationsWithCompletionHandler:"), block.Handle);
        } catch (Exception error) {
            DesktopNotifications.Report(error);
            result.TrySetResult(null);
        }
        return result.Task;
    }

    public void OpenSystemSettings() {
        if (_bundleId is null) return;
        var target = "x-apple.systempreferences:com.apple.Notifications-Settings.extension?id=" + _bundleId;
        // Off the caller's thread: Process.Start can block while another spawn in the app is stalled.
        _ = Task.Run(() => {
            try {
                using var process = Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { target }, UseShellExecute = false });
            } catch (Exception error) { DesktopNotifications.Report(error); }
        });
    }

    public string AcquireCategory(IReadOnlyList<DesktopNotificationAction> actions) {
        using var pool = new Pool();
        return _categories.Acquire(actions);
    }
    public void ReleaseCategory(string category) {
        using var pool = new Pool();
        _categories.Release(category);
    }

    public void Publish(DesktopNotification source, string nativeId, string category, int? badge, Action<string?> completed) {
        using var pool = new Pool();
        var content = Send(GetClass("UNMutableNotificationContent"), Selector("new"));
        try {
            SendVoid(content, Selector("setTitle:"), String(source.Title));
            SendVoid(content, Selector("setBody:"), String(source.Body));
            SendVoid(content, Selector("setCategoryIdentifier:"), String(category));
            if (badge is { } count) SendVoid(content, Selector("setBadge:"),
                Send(GetClass("NSNumber"), Selector("numberWithInteger:"), count));
            var request = Send(GetClass("UNNotificationRequest"), Selector("requestWithIdentifier:content:trigger:"), String(nativeId), content, 0);
            using var block = MacNotificationBlock.Completion(error => completed(Error(error)));
            SendVoid(_center, Selector("addNotificationRequest:withCompletionHandler:"), request, block.Handle);
        } finally { Release(content); }
    }

    public void SetBadge(int count, Action<bool> completed) {
        // The shared center outlives our delegate, including queued shutdown clears.
        if (_center == 0 || !OperatingSystem.IsMacOSVersionAtLeast(13)) { completed(false); return; }
        try {
            using var pool = new Pool();
            using var block = MacNotificationBlock.Completion(error => {
                var failure = Error(error);
                if (failure is not null) DesktopNotifications.Report(failure);
                completed(failure is null);
            });
            SendVoid(_center, Selector("setBadgeCount:withCompletionHandler:"), count, block.Handle);
        } catch (Exception error) {
            DesktopNotifications.Report(error);
            completed(false);
        }
    }

    static nint CreateCategory(string key, IReadOnlyList<DesktopNotificationAction> actions) {
        var nativeActions = Array(actions.Select(a => Send(GetClass("UNNotificationAction"), Selector("actionWithIdentifier:title:options:"),
            String(key + "." + a.Id), String(a.Label), a.Id == "open" ? 4 : 0)));
        var category = Send(GetClass("UNNotificationCategory"), Selector("categoryWithIdentifier:actions:intentIdentifiers:options:"),
            String(key), nativeActions, Array([]), 1); // custom-dismiss callback
        return Send(category, Selector("retain"));
    }

    void RegisterCategories(IReadOnlyCollection<nint> categories) {
        using var pool = new Pool();
        var set = Send(GetClass("NSSet"), Selector("setWithArray:"), Array(categories));
        SendVoid(_center, Selector("setNotificationCategories:"), set);
    }

    public void Withdraw(string nativeId) {
        using var pool = new Pool();
        var identifiers = Array([String(nativeId)]);
        SendVoid(_center, Selector("removePendingNotificationRequestsWithIdentifiers:"), identifiers);
        SendVoid(_center, Selector("removeDeliveredNotificationsWithIdentifiers:"), identifiers);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try { _categories.Dispose(); } catch (Exception error) { DesktopNotifications.Report(error); }
        if (_delegate == 0) return;
        Delegates.TryRemove(_delegate, out _);
        if (Send(_center, Selector("delegate")) == _delegate) SendVoid(_center, Selector("setDelegate:"), 0);
        Release(_delegate);
    }

    static unsafe nint DelegateClass() {
        if (_delegateClass != 0) return _delegateClass;
        var cls = AllocateClass(GetClass("NSObject"), "KcapUserNotificationCenterDelegate", 0);
        if (cls == 0) throw new InvalidOperationException("Unable to create the notification delegate.");
        if (AddProtocol(cls, GetProtocol("UNUserNotificationCenterDelegate")) == 0 ||
            AddMethod(cls, Selector("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&WillPresent, "v@:@@@") == 0 ||
            AddMethod(cls, Selector("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidRespond, "v@:@@@") == 0)
            throw new InvalidOperationException("Unable to register notification callbacks.");
        RegisterClass(cls);
        return _delegateClass = cls;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void WillPresent(nint self, nint selector, nint center, nint notification, nint completion) {
        // macOS calls this only while our app is foreground. Suppress a notification whose
        // asynchronous authorization/delivery raced the user bringing the app forward.
        try {
            if (Delegates.TryGetValue(self, out var reference) && reference.TryGetTarget(out var owner)) {
                var request = Send(notification, Selector("request"));
                var id = Text(Send(request, Selector("identifier")));
                if (id is not null) owner.Suppressed?.Invoke(id);
            }
        } catch (Exception error) { DesktopNotifications.Report(error); }
        finally { MacNotificationBlock.Present(completion, 0); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void DidRespond(nint self, nint selector, nint center, nint response, nint completion) {
        try {
            if (!Delegates.TryGetValue(self, out var reference) || !reference.TryGetTarget(out var sink)) return;
            var notification = Send(response, Selector("notification"));
            var request = Send(notification, Selector("request"));
            var id = Text(Send(request, Selector("identifier")));
            var action = Text(Send(response, Selector("actionIdentifier")));
            if (id is not null) sink.Responded?.Invoke(id, action);
        } catch (Exception error) { DesktopNotifications.Report(error); }
        finally { MacNotificationBlock.Finish(completion); }
    }
}
