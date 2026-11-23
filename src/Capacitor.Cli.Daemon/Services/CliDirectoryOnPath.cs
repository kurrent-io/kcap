namespace Capacitor.Cli.Daemon.Services;

/// <summary>The Windows plugin's hooks start <c>kcap.exe</c> by name, from the search path an agent inherits from
/// this daemon. A unit's search path is captured when the unit is installed, which can be before the directory
/// holding that CLI was put on the user's, and nothing rewrites the unit when it is.</summary>
static class CliDirectoryOnPath {
    const char Separator = ';';

    /// <summary><paramref name="path"/> with <paramref name="cliDirectory"/> searched last, so a CLI the user put
    /// ahead of it still wins; null when the directory is already searched.</summary>
    internal static string? Appended(string? path, string cliDirectory) {
        var directory = Trimmed(cliDirectory);
        if (directory.Length == 0) return null;

        var entries = (path ?? "").Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Any(e => string.Equals(Trimmed(e), directory, StringComparison.OrdinalIgnoreCase))) return null;

        return entries.Length == 0 ? directory : $"{path!.TrimEnd(Separator, ' ')}{Separator}{directory}";
    }

    static string Trimmed(string directory) => directory.Trim().TrimEnd('\\', '/');
}
