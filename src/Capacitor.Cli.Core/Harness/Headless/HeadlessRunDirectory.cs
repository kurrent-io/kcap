namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>An owner-only, initially empty directory for one headless run. Disposing removes it without
/// following a link out of it.</summary>
public sealed class HeadlessRunDirectory : IDisposable {
    internal HeadlessRunDirectory(string path) => Path = path;

    public string Path { get; }

    /// <summary>Creates <paramref name="name"/> inside this directory, owner-only and verified empty.</summary>
    public HeadlessRunDirectory Child(string name) {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException($"'{name}' is not a single directory name.", nameof(name));

        return HeadlessRunRoot.CreatePrivate(System.IO.Path.Combine(Path, name));
    }

    public void Dispose() {
        try {
            NoFollowTree.Delete(Path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Left for the next daemon start's sweep.
        }
    }
}
