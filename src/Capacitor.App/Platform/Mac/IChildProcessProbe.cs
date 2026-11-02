namespace Capacitor.App.Platform.Mac;

/// The kernel's view of this process's direct children.
public interface IChildProcessProbe {
    IReadOnlyList<int> Children();

    /// False for a fork that has not reached exec yet; null for a pid that is gone, a zombie, or unreadable.
    bool? HasExeced(int pid);

    /// Distinguishes one incarnation of a pid from a later process that reuses it.
    string? Identity(int pid);

    void Kill(int pid);
}
