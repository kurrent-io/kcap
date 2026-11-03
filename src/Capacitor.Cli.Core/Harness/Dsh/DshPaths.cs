namespace Capacitor.Cli.Core.Harness.Dsh;

/// <summary>
/// Filesystem layout for DeepSeek Harness (dsh). dsh keeps its own state under <c>$DSH_HOME</c>
/// (else <c>~/.dsh</c>); the session copies kcap records live under kcap's cache instead, because
/// dsh's own logs are zstd-compressed and only the kcap plugin writes them as plain JSONL.
/// </summary>
public sealed class DshPaths {
    public DshPaths(UserHome home, string? dshHome) {
        Home        = !string.IsNullOrWhiteSpace(dshHome) ? dshHome : Path.Combine(home.Path, ".dsh");
        SessionsDir = Path.Combine(home.Path, ".cache", "kcap", "dsh");
    }

    public string Home { get; }

    /// <summary>Per-session JSONL the kcap plugin appends to and the watcher tails. The plugin
    /// resolves it from <c>os.homedir()</c>, never from <c>$DSH_HOME</c>.</summary>
    public string SessionsDir { get; }

    public string SessionJsonl(string rawSessionId) => Path.Combine(SessionsDir, $"{rawSessionId}.jsonl");

    /// <summary>The plugin gets a directory of its own with its own <c>package.json</c>: dsh's
    /// compatibility check walks up from the module to the nearest manifest, and an unrelated one
    /// above <c>$DSH_HOME</c> could carry requirements that disable the plugin.</summary>
    public string PluginDir => Path.Combine(Home, "plugins", "kcap");

    public string PluginFile => Path.Combine(PluginDir, "index.mjs");

    public string PluginManifest => Path.Combine(PluginDir, "package.json");

    /// <summary>The home patch layer, applied to every profile after its own layer.</summary>
    public string CordisPatch => Path.Combine(Home, "cordis.patch.yml");

    /// <summary>Profiles carry their own <c>cordis.patch.yml</c>; an older kcap registered the
    /// plugin in each of them, which uninstall still cleans up.</summary>
    public string ProfilesDir => Path.Combine(Home, "profiles");

    /// <summary>Where an older kcap installed the plugin file, removed on install and uninstall.</summary>
    public string LegacyPluginFile => Path.Combine(Home, "kcap-dsh.plugin.mjs");

    /// <summary>Whether dsh has run here: it creates profiles or session logs on first start.</summary>
    public bool HasUserData() =>
        Directory.Exists(Home)
     && (Directory.Exists(ProfilesDir) || Directory.Exists(Path.Combine(Home, "sessions")));
}
