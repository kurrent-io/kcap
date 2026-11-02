namespace Capacitor.Cli.Services;

/// <summary>
/// Reads gcloud's own on-disk configuration for the active default project — no process spawn, no
/// network. Read by the CLI as it stamps a daemon spawn's environment — the <c>daemon service
/// install</c> capture and a direct <c>daemon start</c> / <c>-d</c> alike; the daemon process itself
/// never reads it.
/// </summary>
static class GcloudConfig {
    /// <summary>gcloud's configuration directory, resolved as gcloud resolves it.</summary>
    public static string? ConfigDir(Core.UserHome home) =>
        ConfigDir(home, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable);

    /// <summary><c>CLOUDSDK_CONFIG</c> when it names an absolute path, else <c>%APPDATA%\gcloud</c> on
    /// Windows and <c>~/.config/gcloud</c> elsewhere. Null when that cannot be resolved — a relative
    /// override, or Windows with no <c>APPDATA</c> — so nothing is derived from a guessed location.</summary>
    internal static string? ConfigDir(Core.UserHome home, bool isWindows, Func<string, string?> env) {
        if (env("CLOUDSDK_CONFIG") is { Length: > 0 } configured)
            return IsAbsolute(configured, isWindows) ? configured : null;

        if (!isWindows) return Path.Combine(home.Path, ".config", "gcloud");

        return env("APPDATA") is { Length: > 0 } appData && IsAbsolute(appData, isWindows)
            ? Path.Combine(appData, "gcloud")
            : null;
    }

    /// <summary>Judged by the target platform's rules rather than the host's, so either arm is testable
    /// anywhere.</summary>
    static bool IsAbsolute(string path, bool isWindows) {
        if (!isWindows) return path.StartsWith('/');

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');
    }

    /// <summary>The active configuration's <c>[core] project</c> under <paramref name="root"/> (see
    /// <see cref="ConfigDir(Core.UserHome)"/>), or null when gcloud is not set up, the config is
    /// unreadable, or no project is set. Never throws.</summary>
    public static string? DefaultProject(string? root) => DefaultProject(root, Environment.GetEnvironmentVariable);

    /// <summary>The active configuration is picked as gcloud picks it: <c>CLOUDSDK_ACTIVE_CONFIG_NAME</c>,
    /// then the <c>active_config</c> file, then <c>default</c>. A name gcloud would refuse derives
    /// nothing, since it is joined into a path.</summary>
    internal static string? DefaultProject(string? root, Func<string, string?> env) {
        if (root is null) return null;

        try {
            var active = "default";

            var activePath = Path.Combine(root, "active_config");
            if (env("CLOUDSDK_ACTIVE_CONFIG_NAME") is { Length: > 0 } overridden)
                active = overridden;
            else if (File.Exists(activePath) && File.ReadAllText(activePath).Trim() is { Length: > 0 } name)
                active = name;

            if (!IsConfigurationName(active)) return null;

            var configPath = Path.Combine(root, "configurations", $"config_{active}");

            return File.Exists(configPath) ? ParseProject(File.ReadAllText(configPath)) : null;
        } catch {
            return null;
        }
    }

    /// <summary>gcloud's own rule for a configuration name: a lowercase letter, then lowercase letters,
    /// digits and hyphens.</summary>
    static bool IsConfigurationName(string name) =>
        char.IsAsciiLetterLower(name[0]) && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>Minimal INI walk: the <c>project</c> key inside the <c>[core]</c> section and
    /// nowhere else — the same key under another section (e.g. <c>[compute]</c>) means something
    /// different to gcloud and must not be read as the default project.
    ///
    /// <para>Two keys in one section is a file gcloud's own reader refuses, so this refuses it too
    /// rather than picking a winner it cannot know: a wrong project is baked into a unit and surfaces
    /// much later as an auth failure, where deriving nothing surfaces immediately as a partial trio.
    /// Whole-line comments, trailing comments and a quoted value are all stripped — hand-edited files
    /// carry all three, and each would otherwise be baked into the value verbatim.</para></summary>
    internal static string? ParseProject(string ini) {
        var inCore = false;
        string? found = null;

        foreach (var raw in ini.Split('\n')) {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] is '#' or ';') continue;

            if (line.StartsWith('[')) {
                inCore = line.Equals("[core]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inCore) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].Trim().Equals("project", StringComparison.OrdinalIgnoreCase)) continue;

            if (found is not null) return null;

            found = CleanValue(line[(eq + 1)..]);
        }

        return found;
    }

    /// <summary>A trailing <c>#</c>/<c>;</c> comment removed, then one matching pair of surrounding
    /// quotes. Null for an empty result, which is the same as the key being absent.</summary>
    static string? CleanValue(string raw) {
        var cut   = raw.IndexOfAny(['#', ';']);
        var value = (cut >= 0 ? raw[..cut] : raw).Trim();

        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'')
            value = value[1..^1].Trim();

        return value.Length > 0 ? value : null;
    }
}
