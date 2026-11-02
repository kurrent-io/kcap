namespace Capacitor.App.Platform.Mac;

/// The kernel's view of this process's direct children.
public interface IChildProcessProbe {
    /// This process's own executable, or null when it cannot be read.
    string? SelfImage { get; }

    IReadOnlyList<int> Children();

    /// Null for a pid that is gone or already a zombie.
    string? ImagePath(int pid);

    /// Distinguishes one incarnation of a pid from a later process that reuses it.
    string? Identity(int pid);

    void Kill(int pid);
}
