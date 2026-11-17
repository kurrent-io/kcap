namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// The version stamp kcap keeps beside a file it installed into a vendor's config: a sibling file named
/// <c>markerFileName</c> holding <see cref="CapacitorVersion.Current"/>, with no trailing newline. Reading,
/// writing and deleting are best effort: a missing or unreadable marker only means the next refresh
/// reinstalls.
/// </summary>
public static class InstallMarker {
    /// <summary>The trimmed marker contents, or null when there is none or it cannot be read.</summary>
    public static string? Read(string configPath, string markerFileName) =>
        PathFor(configPath, markerFileName) is { } marker ? ReadAt(marker) : null;

    public static void Write(string configPath, string markerFileName) {
        var dir = Path.GetDirectoryName(configPath);
        if (string.IsNullOrEmpty(dir)) return;

        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, markerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void Delete(string configPath, string markerFileName) {
        if (PathFor(configPath, markerFileName) is not { } marker) return;

        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* best effort */ }
    }

    public static bool Exists(string configPath, string markerFileName) =>
        PathFor(configPath, markerFileName) is { } marker && File.Exists(marker);

    static string? PathFor(string configPath, string markerFileName) =>
        Path.GetDirectoryName(configPath) is { Length: > 0 } dir ? Path.Combine(dir, markerFileName) : null;

    static string? ReadAt(string marker) {
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; } catch { return null; }
    }
}
