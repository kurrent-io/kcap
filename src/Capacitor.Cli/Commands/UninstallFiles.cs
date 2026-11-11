using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

static class UninstallFiles {
    static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // The local root retains its lifecycle lock and retired-origin registry, and the user-wide unit lock and record of
    // retired ports stay too. Deleting any of them would let concurrent installers take different locks or reuse a
    // retired endpoint.
    public static void DeleteConfig(string directory, string? localRoot, IReadOnlyCollection<string>? keepFiles = null) {
        string[] files = keepFiles is null ? [] : [.. keepFiles.Where(File.Exists).Select(Physical)];
        Delete(directory, localRoot, files);
    }

    /// <summary>Whether <paramref name="file"/> lies inside <paramref name="directory"/>, compared where both physically
    /// are: the user-wide files sit under the home's <c>.config</c>, which a linked ancestor can make the same place as a
    /// differently spelled config directory.</summary>
    public static bool Holds(string directory, string file) =>
        CanonicalPath.TryResolve(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), out var physical)
        && Physical(file).StartsWith(physical + Path.DirectorySeparatorChar, Comparison);

    // Ancestors are resolved but the leaf is not, so a kept file is matched where it lies, as the entries beside it are.
    static string Physical(string path) {
        var full = Path.GetFullPath(path);

        return Path.GetDirectoryName(full) is { } parent ? Path.Join(CanonicalPath.Resolve(parent), Path.GetFileName(full)) : full;
    }

    static void Delete(string directory, string? localRoot, string[] files) {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var keep = localRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        if (!CanonicalPath.TryResolve(path, out var physicalPath)
            || (keep is not null && !CanonicalPath.TryResolve(keep, out _)))
            throw new IOException("Cannot resolve the configuration and local installation paths safely.");
        var physicalKeep = keep is null ? null : CanonicalPath.Resolve(keep);
        if (physicalKeep is not null && physicalPath.Equals(physicalKeep, Comparison)) return;
        var holdsRoot = keep is not null && physicalKeep!.StartsWith(physicalPath + Path.DirectorySeparatorChar, Comparison);
        var holdsFile = files.Any(file => file.StartsWith(physicalPath + Path.DirectorySeparatorChar, Comparison));
        if (holdsRoot || holdsFile) {
            if (new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException($"Cannot remove linked config directory {path} while preserving the local installation.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) {
                if (Directory.Exists(entry)) Delete(entry, keep, files);
                else if (!files.Any(file => Path.Join(physicalPath, Path.GetFileName(entry)).Equals(file, Comparison))) File.Delete(entry);
            }
            return;
        }
        Directory.Delete(path, recursive: true);
    }
}
