using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Capacitor.App.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsSingleInstanceLock : SingleInstanceLock {
    readonly Mutex _mutex;
    readonly EventWaitHandle _showRequest;
    readonly EventWaitHandle _uninstallRequest;
    readonly RegisteredWaitHandle _registration;
    readonly RegisteredWaitHandle _uninstallRegistration;

    readonly HeldRequest _uninstall = new();

    public override event Action? ShowRequested;

    /// The lock is held before the app can subscribe, and the launch that asked has exited by then, so a request
    /// that arrives in between is held for the first subscriber.
    public override event Action? UninstallRequested {
        add => _uninstall.Listen(value);
        remove => _uninstall.Ignore(value);
    }

    WindowsSingleInstanceLock(Mutex mutex, EventWaitHandle showRequest, EventWaitHandle uninstallRequest) {
        _mutex = mutex;
        _showRequest = showRequest;
        _uninstallRequest = uninstallRequest;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showRequest, (_, _) => ShowRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
        _uninstallRegistration = ThreadPool.RegisterWaitForSingleObject(
            _uninstallRequest, (_, _) => _uninstall.Raise(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    // The path is hashed because a kernel object name cannot contain a backslash.
    public static string NameFor(string executablePath) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executablePath).ToUpperInvariant()));
        return $"kcap-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
    }

    public static SingleInstanceLock? AcquireForThisExecutable(TimeSpan waitForRelease, bool uninstall = false) =>
        Environment.ProcessPath is { } exe ? TryAcquire(NameFor(exe), waitForRelease, uninstall) : Unenforced;

    /// <param name="uninstall">What to ask of a copy that already holds the lock: its uninstall rather than its window.</param>
    public static WindowsSingleInstanceLock? TryAcquire(string name, TimeSpan wait, bool uninstall = false) {
        var mutex = new Mutex(false, $@"Local\{name}");
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.show");
        var uninstallRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.uninstall");
        bool owned;
        try {
            owned = mutex.WaitOne(wait);
        } catch (AbandonedMutexException) {
            owned = true; // the previous owner died without releasing; the lock is ours now
        }
        if (owned) return new WindowsSingleInstanceLock(mutex, showRequest, uninstallRequest);

        // The running copy is a background process, which Windows forbids from taking the
        // foreground unless the process the user just started grants it.
        AllowSetForegroundWindow(AsfwAny);
        (uninstall ? uninstallRequest : showRequest).Set();
        showRequest.Dispose();
        uninstallRequest.Dispose();
        mutex.Dispose();
        return null;
    }

    // The mutex is not released here: ownership is thread-affine and Dispose may run on another
    // thread. Process exit abandons it, which the next TryAcquire treats as free.
    public override void Dispose() {
        _registration.Unregister(null);
        _uninstallRegistration.Unregister(null);
        _showRequest.Dispose();
        _uninstallRequest.Dispose();
        _mutex.Dispose();
        base.Dispose();
    }

    const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
