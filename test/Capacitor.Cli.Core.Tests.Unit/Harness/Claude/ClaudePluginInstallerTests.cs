using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Claude;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudePluginInstallerTests {
    // ── IsEffectivelyInstalled — the destructive-action gate ─────────────────
    //
    // Resolves the enabled plugin's INSTALLED payload the way Claude loads it
    // (plugins/installed_plugins.json → installPath; directory-sourced marketplaces live
    // via known_marketplaces.json installLocation). A marker, an enabled flag alone, or a
    // marketplace SOURCE dir prove nothing about what Claude actually loads.

    static string WriteEnabledSettings(string home) {
        var settingsPath = Path.Combine(home, "settings.json");
        File.WriteAllText(settingsPath, """{ "enabledPlugins": { "kcap@kcap": true } }""");
        return settingsPath;
    }

    static void WriteInstallRecord(string home, string installPath) =>
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(home, "plugins")).FullName,
                                       "installed_plugins.json"), $$"""
            { "version": 2, "plugins": { "kcap@kcap": [
                { "scope": "user", "installPath": {{JsonValue.Create(installPath).ToJsonString()}}, "version": "1.0.0" } ] } }
            """);

    // ── RunsHooks — whether a launch can count on the plugin's hooks firing ──

    [Test]
    public async Task RunsHooks_true_when_the_installed_cache_ships_the_payload() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        var cache        = tmp.CreateDir("cache");
        tmp.CreateFile("cache/.mcp.json", "{}");
        WriteInstallRecord(tmp.Path, cache);

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsTrue();
    }

    /// Claude installs an enabled plugin on its first start, so the machine that has never run it
    /// has no record yet and its hooks still fire. Calling this unwired would leave exactly that
    /// first start on the output checks a first-run screen passes.
    [Test]
    public async Task RunsHooks_true_before_claude_has_first_installed_the_enabled_plugin() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsTrue();
    }

    [Test]
    public async Task RunsHooks_false_when_the_install_record_has_lost_its_payload() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        WriteInstallRecord(tmp.Path, tmp.PathTo("gone"));

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsFalse();
    }

    [Test]
    public async Task RunsHooks_false_when_the_plugin_is_not_enabled() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", "{}");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsFalse();
    }

    /// Only a confirmed absence means Claude has yet to install it: a corrupt record proves nothing,
    /// and calling it a first install would hold a launch prompt for a hook that may never fire.
    [Test]
    public async Task RunsHooks_false_when_the_install_record_cannot_be_read() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        tmp.CreateFile("plugins/installed_plugins.json", "{ not json");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsFalse();
    }

    /// `kcap plugin install --project` enables the plugin in the repository's settings.local.json
    /// and leaves the user's settings alone.
    [Test]
    public async Task RunsHooks_true_when_only_the_project_enables_the_plugin() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", "{}");
        tmp.CreateFile("project/.claude/settings.local.json", """{ "enabledPlugins": { "kcap@kcap": true } }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsTrue();
    }

    /// Project settings outrank the user's, so a repository can switch the plugin off, and a launch
    /// there must not wait for a hook that will not fire.
    [Test]
    public async Task RunsHooks_false_when_the_project_turns_off_a_plugin_the_user_enabled() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        tmp.CreateFile("project/.claude/settings.json", """{ "enabledPlugins": { "kcap@kcap": false } }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsFalse();
    }

    /// Claude treats each plugin id on its own, so switching off a legacy id in a repository leaves
    /// the current one running. Merging the ids would drop this launch from the hold and from arrival
    /// confirmation, and a first-run screen could take the prompt with no sign of it.
    [Test]
    public async Task RunsHooks_keeps_project_precedence_to_the_same_plugin_id() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        tmp.CreateFile("project/.claude/settings.json", """{ "enabledPlugins": { "kapacitor@kurrent": false } }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsTrue();
    }

    /// Whether Claude can load a project's plugin is not something kcap can see from here. Doubt counts
    /// as wired: a wrong hold shows its warning and the terminal, while a wrong paste can lose the
    /// prompt to a first-run screen with no sign at all.
    [Test]
    public async Task RunsHooks_true_for_an_enabled_project_plugin_whatever_its_marketplace_path_says() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", "{}");
        tmp.CreateFile("project/.claude/settings.local.json", ProjectInstall(tmp.PathTo("moved")));

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsTrue();
    }

    static string ProjectInstall(string marketplacePath) => $$"""
        { "enabledPlugins": { "kcap@kcap": true },
          "extraKnownMarketplaces": { "kcap": { "source": { "source": "directory", "path": {{JsonValue.Create(marketplacePath).ToJsonString()}} } } } }
        """;

    /// A second enabled id still loads when the project switches off the first, and it is that id's
    /// own payload that has to be checked.
    [Test]
    public async Task RunsHooks_true_when_a_second_user_plugin_id_survives_the_project() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", """{ "enabledPlugins": { "kcap@kcap": true, "kapacitor@kurrent": true } }""");
        string cache     = tmp.CreateDir("cache");
        tmp.CreateFile("cache/.mcp.json", "{}");
        tmp.CreateFile("plugins/installed_plugins.json", $$"""
            { "version": 2, "plugins": { "kapacitor@kurrent": [
                { "scope": "user", "installPath": {{JsonValue.Create(cache).ToJsonString()}}, "version": "1.0.0" } ] } }
            """);
        tmp.CreateFile("project/.claude/settings.json", """{ "enabledPlugins": { "kcap@kcap": false } }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsTrue();
    }

    /// No hook of any plugin fires under this setting, so nothing could ever release a hold, and a
    /// resumed agent has no transcript to fall back on.
    [Test]
    public async Task RunsHooks_false_when_the_user_disables_all_hooks() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", """{ "enabledPlugins": { "kcap@kcap": true }, "disableAllHooks": true }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsFalse();
    }

    [Test]
    public async Task RunsHooks_false_when_the_project_disables_all_hooks() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        tmp.CreateFile("project/.claude/settings.local.json", """{ "disableAllHooks": true }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsFalse();
    }

    /// The nearer scope decides this too, so a repository can turn hooks back on for itself.
    [Test]
    public async Task RunsHooks_true_when_the_project_overrides_the_users_disable_all_hooks() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", """{ "enabledPlugins": { "kcap@kcap": true }, "disableAllHooks": true }""");
        tmp.CreateFile("project/.claude/settings.json", """{ "disableAllHooks": false }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("project"))).IsTrue();
    }

    /// Claude reads a repository's local settings at its root wherever a session starts, so a launch
    /// from a subdirectory runs what a `--project` install enabled there. Missing it leaves that launch
    /// on the output checks a first-run screen passes.
    [Test, ExcludeOn(OS.Windows)]
    public async Task RunsHooks_true_when_only_the_repository_roots_local_settings_enable_the_plugin() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", "{}");
        tmp.CreateDir("repo", ".git");
        tmp.CreateFile("repo/.claude/settings.local.json", """{ "enabledPlugins": { "kcap@kcap": true } }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.CreateDir("repo", "sub"))).IsTrue();
    }

    /// A linked worktree uses the main checkout's file, and a launch that borrows the worktree has
    /// no copy of it.
    [Test, ExcludeOn(OS.Windows)]
    public async Task RunsHooks_true_when_only_the_main_checkouts_local_settings_enable_the_plugin() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", "{}");
        tmp.CreateDir("main", ".git", "worktrees", "linked");
        tmp.CreateFile("main/.claude/settings.local.json", """{ "enabledPlugins": { "kcap@kcap": true } }""");
        tmp.CreateFile("linked/.git", $"gitdir: {tmp.PathTo("main/.git/worktrees/linked")}");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("linked"))).IsTrue();
    }

    /// Claude still reads a local file an older build left in the starting directory, beneath the root's.
    [Test, ExcludeOn(OS.Windows)]
    public async Task RunsHooks_false_when_the_repository_root_turns_off_what_the_launch_directory_enabled() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", "{}");
        tmp.CreateDir("repo", ".git");
        tmp.CreateFile("repo/.claude/settings.local.json", """{ "enabledPlugins": { "kcap@kcap": false } }""");
        tmp.CreateFile("repo/sub/.claude/settings.local.json", """{ "enabledPlugins": { "kcap@kcap": true } }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.PathTo("repo/sub"))).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task RunsHooks_false_when_the_repository_root_disables_all_hooks() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        tmp.CreateDir("repo", ".git");
        tmp.CreateFile("repo/.claude/settings.local.json", """{ "disableAllHooks": true }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.CreateDir("repo", "sub"))).IsFalse();
    }

    /// Only the local file moves to the root: Claude reads the shared one from the session's own
    /// directory, so a hold here would wait for a hook that never fires.
    [Test]
    public async Task RunsHooks_false_for_a_plugin_only_the_repository_roots_shared_settings_enable() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", "{}");
        tmp.CreateDir("repo", ".git");
        tmp.CreateFile("repo/.claude/settings.json", """{ "enabledPlugins": { "kcap@kcap": true } }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.CreateDir("repo", "sub"))).IsFalse();
    }

    /// Windows keeps the local file in the starting directory, so the root's loads nothing there.
    [Test, RunOn(OS.Windows)]
    public async Task RunsHooks_false_on_windows_for_the_repository_roots_local_settings() {
        using var tmp = new TempDir();
        var settingsPath = tmp.CreateFile("settings.json", "{}");
        tmp.CreateDir("repo", ".git");
        tmp.CreateFile("repo/.claude/settings.local.json", """{ "enabledPlugins": { "kcap@kcap": true } }""");

        await Assert.That(ClaudePluginInstaller.RunsHooks(settingsPath, tmp.CreateDir("repo", "sub"))).IsFalse();
    }

    [Test]
    public async Task IsEffectivelyInstalled_true_when_the_installed_cache_ships_the_payload() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        var install = tmp.CreateDir("plugins", "cache", "kcap", "kcap", "1.0.0");
        install.CreateFile(".mcp.json", "{}");
        WriteInstallRecord(tmp.Path, install);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsTrue();
    }

    /// <summary>Directory-sourced marketplaces are resolved live: the install record's cache
    /// path never materializes, and the loadable payload is the marketplace installLocation.</summary>
    [Test]
    public async Task IsEffectivelyInstalled_true_for_directory_marketplace_with_phantom_cache_path() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        WriteInstallRecord(tmp.Path, tmp.PathTo("plugins", "cache", "kcap", "kcap", "1.0.0")); // never created
        var location = tmp.CreateDir("npm-kcap", "kcap");
        location.CreateFile(".mcp.json", "{}");
        tmp.CreateFile(["plugins", "known_marketplaces.json"], $$"""
            { "kcap": { "source": { "source": "directory", "path": {{JsonValue.Create(location.Path).ToJsonString()}} },
                        "installLocation": {{JsonValue.Create(location.Path).ToJsonString()}} } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsTrue();
    }

    /// <summary>
    /// The directory-marketplace exception is for LIVE resolution only. A git/github-sourced
    /// marketplace IS cached: once its installed cache payload is gone, a lingering checkout
    /// under installLocation proves nothing — accepting it would let doctor --clean delete the
    /// user's only working registrations.
    /// </summary>
    [Test]
    public async Task IsEffectivelyInstalled_false_for_git_marketplace_with_lingering_checkout_and_no_cache() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        WriteInstallRecord(tmp.Path, tmp.PathTo("plugins", "cache", "kcap", "kcap", "1.0.0")); // cache gone
        var checkout = tmp.CreateDir("plugins", "marketplaces", "kcap");
        checkout.CreateFile(".mcp.json", "{}"); // stale clone still ships the payload
        tmp.CreateFile(["plugins", "known_marketplaces.json"], $$"""
            { "kcap": { "source": { "source": "github", "repo": "kurrent-io/kcap-cli" },
                        "installLocation": {{JsonValue.Create(checkout.Path).ToJsonString()}} } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    /// <summary>
    /// Both callers gate on the USER-scope settings.json enabled flag, so only a user-scoped
    /// v2 install entry proves that flag's payload — a project/local-scoped install belonging
    /// to some unrelated repo must not make the plugin globally "effective".
    /// </summary>
    [Test]
    public async Task IsEffectivelyInstalled_false_when_only_a_project_scoped_install_has_the_payload() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        var install = tmp.CreateDir("plugins", "cache", "kcap", "kcap", "1.0.0");
        install.CreateFile(".mcp.json", "{}");
        tmp.CreateFile(["plugins", "installed_plugins.json"], $$"""
            { "version": 2, "plugins": { "kcap@kcap": [
                { "scope": "project", "installPath": {{JsonValue.Create(install.Path).ToJsonString()}}, "version": "1.0.0" } ] } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    /// <summary>
    /// The cross-product of the two round-3 gates: a v2 record with ONLY a project-scoped
    /// install must not fall through to the directory-marketplace fallback either — that
    /// fallback only excuses a phantom cache path on an otherwise-eligible (user-scoped)
    /// record, never the absence of an eligible record.
    /// </summary>
    [Test]
    public async Task IsEffectivelyInstalled_false_for_project_only_record_even_with_directory_marketplace() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        var install = tmp.CreateDir("plugins", "cache", "kcap", "kcap", "1.0.0");
        install.CreateFile(".mcp.json", "{}");
        var checkout = tmp.CreateDir("marketplace-src", "kcap");
        checkout.CreateFile(".mcp.json", "{}");
        var pluginsDir = tmp.CreateDir("plugins");
        pluginsDir.CreateFile("installed_plugins.json", $$"""
            { "version": 2, "plugins": { "kcap@kcap": [
                { "scope": "project", "installPath": {{JsonValue.Create(install.Path).ToJsonString()}}, "version": "1.0.0" } ] } }
            """);
        pluginsDir.CreateFile("known_marketplaces.json", $$"""
            { "kcap": { "source": { "source": "directory" },
                        "installLocation": {{JsonValue.Create(checkout.Path).ToJsonString()}} } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    /// <summary>Pre-v2 compatibility: a bare-object install record predates scopes and stays accepted.</summary>
    [Test]
    public async Task IsEffectivelyInstalled_true_for_bare_object_install_record_with_payload() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        var install = tmp.CreateDir("plugins", "cache", "kcap", "kcap", "1.0.0");
        install.CreateFile(".mcp.json", "{}");

        tmp.CreateFile(["plugins", "installed_plugins.json"], $$"""
            { "version": 1, "plugins": { "kcap@kcap":
                { "installPath": {{JsonValue.Create(install.Path).ToJsonString()}}, "version": "1.0.0" } } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsEffectivelyInstalled_false_when_enabled_but_no_install_record() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsEffectivelyInstalled_false_when_only_the_marker_exists() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        tmp.CreateFile(ClaudePluginInstaller.MarkerFileName, "1.2.3");
        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    /// <summary>The round-1 gap this closes: a marketplace SOURCE dir shipping .mcp.json does
    /// not prove the installed payload exists — without an install record it is not effective.</summary>
    [Test]
    public async Task IsEffectivelyInstalled_false_when_only_a_marketplace_source_dir_has_the_payload() {
        using var tmp = new TempDir();
        var source = tmp.CreateDir("source-kcap");
        source.CreateFile(".mcp.json", "{}");
        var settingsPath = tmp.CreateFile("settings.json", $$"""
            { "enabledPlugins": { "kcap@kcap": true },
              "extraKnownMarketplaces": { "kcap": { "source": {
                  "source": "directory", "path": {{JsonValue.Create(source.Path).ToJsonString()}} } } } }
            """);

        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsEffectivelyInstalled_false_when_the_install_record_points_nowhere() {
        using var tmp = new TempDir();
        var settingsPath = WriteEnabledSettings(tmp.Path);
        WriteInstallRecord(tmp.Path, tmp.PathTo("gone")); // no cache, no marketplace record
        await Assert.That(ClaudePluginInstaller.IsEffectivelyInstalled(settingsPath)).IsFalse();
    }
    [Test]
    public async Task IsInstalled_false_when_dir_missing() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("does-not-exist", "settings.json");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_true_when_marker_present() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        tmp.CreateFile(ClaudePluginInstaller.MarkerFileName,
            "1.2.3");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_enabledPlugins_has_kcap() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kcap@kcap": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_marketplace_has_kcap() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "extraKnownMarketplaces": { "kcap": { "source": { "source": "directory", "path": "/some/path" } } } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_legacy_enabledPlugins_kurrent_key_present() {
        // Pre-rename installs used the "kcap@kurrent" key. The installer
        // and remover both treat it as a kcap-owned stale entry, so the
        // refresh gate must detect it too — otherwise users on a pre-marker
        // pre-rename config would never get migrated.
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kcap@kurrent": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_legacy_marketplace_kurrent_key_present() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "extraKnownMarketplaces": { "kurrent": { "source": { "source": "directory", "path": "/some/path" } } } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_pre_rename_kapacitor_enabledPlugins_present() {
        // Pre-rename installs used the "kapacitor@kapacitor" key. Refresh on
        // upgrade must pick this up so the user gets migrated to the kcap
        // marketplace entry.
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kapacitor@kapacitor": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_pre_rename_kapacitor_kurrent_enabledPlugins_present() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "enabledPlugins": { "kapacitor@kurrent": true } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_pre_rename_kapacitor_marketplace_present() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "extraKnownMarketplaces": { "kapacitor": { "source": { "source": "directory", "path": "/some/path" } } } }
            """);
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_settings_has_unrelated_keys_only() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(settingsPath, """{ "theme": "dark" }""");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_false_when_settings_is_malformed() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        await File.WriteAllTextAsync(settingsPath, "{not json");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settingsPath)).IsFalse();
    }

    [Test]
    public async Task WriteMarker_then_ReadMarker_round_trips() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        ClaudePluginInstaller.WriteMarker(settingsPath);
        await Assert.That(ClaudePluginInstaller.ReadMarker(settingsPath))
            .IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task DeleteMarker_removes_file_and_is_idempotent() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");
        ClaudePluginInstaller.WriteMarker(settingsPath);
        ClaudePluginInstaller.DeleteMarker(settingsPath);
        await Assert.That(File.Exists(tmp.PathTo(ClaudePluginInstaller.MarkerFileName))).IsFalse();
        ClaudePluginInstaller.DeleteMarker(settingsPath);
    }
}
