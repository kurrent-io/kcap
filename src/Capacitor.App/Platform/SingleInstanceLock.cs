using Capacitor.App.Platform.Linux;
using Capacitor.App.Platform.Windows;

namespace Capacitor.App.Platform;

public abstract class SingleInstanceLock : IDisposable {
    /// macOS enforces one copy of a bundle itself.
    public static SingleInstanceLock Unenforced { get; } = new UnenforcedSingleInstanceLock();

    /// Null when another copy holds the lock. That copy has been asked to show its window, or to open its uninstall
    /// when <paramref name="uninstall"/> says this launch came from Windows to remove the app.
    public static SingleInstanceLock? Acquire(TimeSpan waitForRelease, bool uninstall = false) {
        if (OperatingSystem.IsWindows()) return WindowsSingleInstanceLock.AcquireForThisExecutable(waitForRelease, uninstall);
        if (OperatingSystem.IsLinux()) return LinuxSingleInstanceLock.AcquireForThisUser(waitForRelease);
        return Unenforced;
    }

    /// Raised on a background thread.
    public abstract event Action? ShowRequested;

    /// Raised on a background thread, only on Windows.
    public virtual event Action? UninstallRequested { add { } remove { } }

    public virtual void Dispose() => GC.SuppressFinalize(this);
}
