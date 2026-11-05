using System.ComponentModel;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Reads, fresh each time, everywhere that can name a kcap install: every launch agent, the job launchd
/// holds for each kcap daemon, the git config, each harness's user-scope integration files, the files kcap wires into
/// each registered account's directory and, in every Claude directory where kcap's plugin is installed, where Claude
/// loads that plugin from and its plugin records.</summary>
internal static class AdoptionReferenceSites {
    /// <summary>Names the site whose text is the folder or <c>.mcp.json</c> Claude loads kcap's plugin from, or null
    /// when it resolves none; it holds an install unless that path is inside the bundle.</summary>
    internal const string ClaudePluginSite = "claude-plugin:";

    /// <summary>A service file, or the job launchd loaded from one: these name an install until its daemon moves, which
    /// no integrations refresh does.</summary>
    internal static bool IsService(ReferenceSite site) =>
        site.Name.StartsWith(LaunchdSite, StringComparison.Ordinal)
     || Path.GetFileName(site.Name) == LaunchAgents
     || Path.GetFileName(Path.GetDirectoryName(site.Name)) == LaunchAgents;

    const string LaunchAgents      = "LaunchAgents";
    const string AccountsSite      = "accounts:";
    const string LaunchdSite       = "launchd:";
    const string DaemonLabelPrefix = "io.kurrent.kcap.daemon.";

    internal static Task<IReadOnlyList<ReferenceSite>> ReadAsync(
            UserHome home, AccountStore accounts, Launchctl launchctl, CancellationToken ct) =>
        ReadAsync(home, HarnessRegistry.FromEnvironment(home), accounts, launchctl, ct);

    internal static async Task<IReadOnlyList<ReferenceSite>> ReadAsync(
            UserHome home, HarnessRegistry harnesses, AccountStore accounts, Launchctl launchctl, CancellationToken ct) {
        var sites  = new Sites();
        var labels = await Task.Run(() => ReadLaunchAgents(Path.Combine(home.Path, "Library", LaunchAgents), sites), ct)
            .ConfigureAwait(false);
        foreach (var label in labels) sites.Add(await PrintAsync(launchctl, label, ct).ConfigureAwait(false));

        await Task.Run(() => {
            sites.AddFile(Path.Combine(home.Path, ".gitconfig"));
            sites.AddFile(Path.Combine(home.Path, ".config", "git", "config"));
            foreach (var harness in harnesses)
                foreach (var file in harness.IntegrationFiles) sites.AddFile(file);
            ReadAccounts(home, accounts, sites);
        }, ct).ConfigureAwait(false);

        return sites.All;
    }

    // A registry that cannot be read is an unreadable site: any directory it lists could name the install. The default
    // Claude directory is read whether or not it is registered; Codex's default is among the harnesses' own files.
    static void ReadAccounts(UserHome home, AccountStore accounts, Sites sites) {
        var registry = accounts.TryLoad();
        if (registry is null) sites.Add(new ReferenceSite(AccountsSite + accounts.RegistryPath, null, false));
        var listed = registry?.Accounts ?? [];

        var claudeHomes = listed.Where(a => a.Vendor == HarnessId.Claude).Select(a => a.Directory)
            .Prepend(AccountLayouts.DefaultDirectory(HarnessId.Claude, home))
            .DistinctBy(AccountDirectory.Normalize);
        foreach (var claudeHome in claudeHomes) ReadClaude(AccountLayouts.Claude(home, claudeHome), sites);

        foreach (var codex in listed.Where(a => a.Vendor == HarnessId.Codex))
            foreach (var file in CodexHarness.Over(AccountLayouts.Codex(home, codex.Directory)).IntegrationFiles) sites.AddFile(file);
    }

    static List<string> ReadLaunchAgents(string folder, Sites sites) {
        if (!Directory.Exists(folder)) return [];

        string[] plists;
        try {
            plists = [.. Directory.EnumerateFiles(folder, "*.plist").Order(StringComparer.Ordinal)];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            sites.Add(new ReferenceSite(folder, null, false));
            return [];
        }

        foreach (var plist in plists) sites.AddFile(plist);

        return [.. plists.Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(l => l.Length > DaemonLabelPrefix.Length && l.StartsWith(DaemonLabelPrefix, StringComparison.Ordinal))];
    }

    // launchd keeps running the program it loaded, whatever the plist now says.
    static async Task<ReferenceSite> PrintAsync(Launchctl launchctl, string label, CancellationToken ct) {
        var name = LaunchdSite + label;
        try {
            var result = await launchctl.PrintAsync(label, ct).ConfigureAwait(false);
            if (LaunchdPrint.Classify(result.ExitCode, result.Stdout, result.Stderr) != LabelProbe.Loaded) return new(name, null, true);

            return LaunchdPrint.LoadedProgram(result.Stdout) is { } program ? new(name, program, true) : new(name, null, false);
        } catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception) {
            return new(name, null, false);
        }
    }

    // A Claude directory without kcap's plugin is never repointed, so its records are not read: they would hold the
    // install for good. Records that cannot be parsed hold it, since what they say Claude loads is then unknown.
    static void ReadClaude(ClaudePaths paths, Sites sites) {
        foreach (var file in ClaudeHarness.Over(paths).IntegrationFiles) sites.AddFile(file);
        if (!ClaudePluginInstaller.IsInstalled(paths.UserSettings)) return;

        var loads = ClaudePluginInstaller.EffectiveMcpJsonPath(paths.UserSettings)
                 ?? ClaudePluginInstaller.RegisteredMarketplacePath(paths.UserSettings);
        sites.Add(new ReferenceSite(ClaudePluginSite + paths.Home, loads, true));

        var plugins = Path.Combine(paths.Home, "plugins");
        sites.AddFile(Path.Combine(plugins, "known_marketplaces.json"), json: true);
        sites.AddFile(Path.Combine(plugins, "installed_plugins.json"), json: true);
    }

    sealed class Sites {
        readonly HashSet<string> _files = new(StringComparer.Ordinal);

        public List<ReferenceSite> All { get; } = [];

        public void Add(ReferenceSite site) => All.Add(site);

        public void AddFile(string path, bool json = false) {
            if (_files.Add(path)) All.Add(Read(path, json));
        }

        // A missing file names nothing; one that exists and cannot be read might.
        static ReferenceSite Read(string path, bool json) {
            try {
                var text = File.ReadAllTextShared(path);
                if (json) JsonDocument.Parse(text).Dispose();

                return new ReferenceSite(path, text, true);
            } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
                return new ReferenceSite(path, null, true);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                return new ReferenceSite(path, null, false);
            }
        }
    }
}
