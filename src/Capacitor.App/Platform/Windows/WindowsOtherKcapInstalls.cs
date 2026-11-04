using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Platform.Windows;

/// Finds the `kcap` commands a new terminal would reach outside this install, which the uninstall leaves in place.
public static class WindowsOtherKcapInstalls {
    const string NpmRemoveCommand = "npm uninstall -g @kurrent/kcap";
    static readonly string[] Names = ["kcap.exe", "kcap.cmd"];

    /// <param name="terminalPath">A new terminal's PATH, <c>;</c>-separated.</param>
    public static IReadOnlyList<OtherKcapInstall> Find(string? terminalPath, string installRoot) {
        if (string.IsNullOrEmpty(terminalPath)) return [];

        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<OtherKcapInstall>();

        foreach (var entry in terminalPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var dir = Environment.ExpandEnvironmentVariables(entry.Trim('"'));
            if (!Path.IsPathFullyQualified(dir) || LegacyPaths.Within(dir, installRoot) || !seen.Add(LegacyPaths.Normalize(dir))) continue;
            if (Names.Select(name => Path.Combine(dir, name)).FirstOrDefault(File.Exists) is not { } command) continue;

            // npm's global prefix keeps the package beside the shims it puts on PATH.
            var npm = Directory.Exists(Path.Combine(dir, "node_modules", "@kurrent", "kcap"));
            found.Add(new OtherKcapInstall(command, npm ? NpmRemoveCommand : null));
        }

        return found;
    }
}
