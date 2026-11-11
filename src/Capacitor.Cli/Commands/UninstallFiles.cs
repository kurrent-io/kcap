using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

static class UninstallFiles {
    // The local root retains its lifecycle lock and retired-origin registry, and the user-wide unit lock and record of
    // retired ports stay too. Deleting any of them would let concurrent installers take different locks or reuse a
    // retired endpoint.
    public static void DeleteConfig(string directory, string? localRoot, IReadOnlyCollection<string>? keepFiles = null) {
        string[] files = keepFiles is null ? [] : [.. keepFiles.Where(File.Exists).Select(Path.GetFullPath)];
        Delete(directory, localRoot, files, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    static void Delete(string directory, string? localRoot, string[] files, StringComparison comparison) {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var keep = localRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        if (!CanonicalPath.TryResolve(path, out var physicalPath)
            || (keep is not null && !CanonicalPath.TryResolve(keep, out _)))
            throw new IOException("Cannot resolve the configuration and local installation paths safely.");
        var physicalKeep = keep is null ? null : CanonicalPath.Resolve(keep);
        if (physicalKeep is not null && physicalPath.Equals(physicalKeep, comparison)) return;
        var holdsRoot = keep is not null && physicalKeep!.StartsWith(physicalPath + Path.DirectorySeparatorChar, comparison);
        var holdsFile = files.Any(file => file.StartsWith(path + Path.DirectorySeparatorChar, comparison));
        if (holdsRoot || holdsFile) {
            if (new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException($"Cannot remove linked config directory {path} while preserving the local installation.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) {
                if (Directory.Exists(entry)) Delete(entry, keep, files, comparison);
                else if (!files.Any(file => Path.GetFullPath(entry).Equals(file, comparison))) File.Delete(entry);
            }
            return;
        }
        Directory.Delete(path, recursive: true);
    }
}
