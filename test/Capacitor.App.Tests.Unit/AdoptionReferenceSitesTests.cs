using System.Text.Json.Nodes;
using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

public class AdoptionReferenceSitesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>Answers every <c>launchctl print</c> with a job running <paramref name="program"/>, or with no such job.</summary>
    sealed class Launchd(string? program = null, int exitCode = 0) : IProcessRunner {
        public readonly List<string> Printed = [];

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Printed.Add(args[^1]);
            return Task.FromResult(exitCode != 0
                ? new ProcessResult(exitCode, "", "launchctl: internal error", false)
                : program is null
                    ? new ProcessResult(113, "", $"Could not find service \"{args[^1]}\" in domain for user gui: 501", false)
                    : new ProcessResult(0, $"state = running\npid = 321\nprogram = {program}\n", "", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    UserHome Home => new(Tmp.PathTo("home"));
    string Bundle => Tmp.GetResolvedPath("Kcap.app");
    string BundlePlugin => Path.Combine(Bundle, "Contents", "Resources", "plugin");
    string BundleDaemon => Path.Combine(Bundle, "Contents", "MacOS", "kcap-daemon");
    string Claude => Tmp.PathTo("home", ".claude");
    HarnessRegistry Harnesses => TestHarnesses.Under(Home);
    AccountStore Accounts => new(Tmp.PathTo("accounts"));

    OtherCliInstall Npm() {
        var scope    = Tmp.GetResolvedPath("prefix", "lib", "node_modules", "@kurrent");
        var root     = Path.Combine(scope, "kcap");
        var platform = Path.Combine(scope, "kcap-darwin-arm64");
        return new OtherCliInstall(OtherCliInstallKind.Npm, root, "1.2.3", Path.Combine(platform, "bin", "kcap"),
            Path.Combine(platform, "bin", "kcap-daemon"), "id", [root, platform]);
    }

    string Plist(string label, string program) =>
        Tmp.CreateFile($"home/Library/LaunchAgents/{label}.plist", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>Label</key><string>{label}</string>
              <key>ProgramArguments</key><array><string>{program}</string></array>
            </dict></plist>
            """);

    Task<IReadOnlyList<ReferenceSite>> Read(Launchd? launchd = null) =>
        AdoptionReferenceSites.ReadAsync(Home, Harnesses, Accounts, new Launchctl(launchd ?? new Launchd(), "gui/501"), CancellationToken.None);

    async Task<string?> Holder(Launchd? launchd = null) =>
        AdoptionConsumers.Holder(Npm(), DateTime.UtcNow, [], await Read(launchd), Harnesses, Bundle);

    static string Settings(string marketplace) => new JsonObject {
        ["enabledPlugins"]         = new JsonObject { ["kcap@kcap"] = true },
        ["extraKnownMarketplaces"] = new JsonObject {
            ["kcap"] = new JsonObject { ["source"] = new JsonObject { ["source"] = "directory", ["path"] = marketplace } },
        },
    }.ToJsonString();

    static void Write(string path, string text) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Only_kcap_daemon_labels_are_printed() {
        Plist("io.kurrent.kcap.daemon.work", BundleDaemon);
        Plist("io.kurrent.kcap.updater", BundleDaemon);
        var custom  = Plist("com.example.sync", "/usr/local/bin/sync");
        var launchd = new Launchd();

        var sites = await Read(launchd);

        await Assert.That(launchd.Printed).IsEquivalentTo(["gui/501/io.kurrent.kcap.daemon.work"]);
        await Assert.That(sites.Any(s => s.Name == custom)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Every_launch_agent_naming_the_install_holds_it() {
        var custom = Plist("com.example.sync", Npm().CliBinary);

        await Assert.That(await Holder()).IsEqualTo($"site:{custom}");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_loaded_job_still_naming_the_old_program_holds_it() {
        Plist("io.kurrent.kcap.daemon.work", BundleDaemon);

        await Assert.That(await Holder(new Launchd(Npm().DaemonBinary))).IsEqualTo("site:launchd:io.kurrent.kcap.daemon.work");
        await Assert.That(await Holder(new Launchd(BundleDaemon))).IsNull();
        await Assert.That(await Holder(new Launchd())).IsNull();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_job_whose_state_cannot_be_read_holds_it() {
        Plist("io.kurrent.kcap.daemon.work", BundleDaemon);

        await Assert.That(await Holder(new Launchd(exitCode: 5))).IsEqualTo("site:launchd:io.kurrent.kcap.daemon.work");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_harness_file_or_the_git_config_naming_the_install_holds_it() {
        var codex = Tmp.CreateFile("home/.codex/config.toml", $"[mcp_servers.kcap-review]\ncommand = \"{Npm().CliBinary}\"\n");
        await Assert.That(await Holder()).IsEqualTo($"site:{codex}");

        File.WriteAllText(codex, "");
        var git = Tmp.CreateFile("home/.gitconfig", $"[hook \"kcap\"]\n\tcommand = '{Npm().CliBinary}' git-hook\n");
        await Assert.That(await Holder()).IsEqualTo($"site:{git}");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task No_Claude_plugin_passes_and_an_enabled_plugin_with_no_install_record_passes_once_settings_name_the_bundle() {
        var settings = Path.Combine(Claude, "settings.json");
        var known    = Path.Combine(Claude, "plugins", "known_marketplaces.json");

        Write(settings, "{}");
        await Assert.That(await Holder()).IsNull();

        Write(settings, """{"enabledPlugins":{"kcap@kcap":true}}""");
        await Assert.That(await Holder()).IsEqualTo($"site:claude-plugin:{Claude}");

        Write(settings, Settings(BundlePlugin));
        await Assert.That(await Holder()).IsNull();

        var old = Path.Combine(Npm().Root, "plugin");
        Write(known, $$$"""{"kcap":{"source":{"source":"directory","path":"{{{old}}}"},"installLocation":"{{{old}}}"}}""");
        await Assert.That(await Holder()).IsEqualTo($"site:{known}");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task An_effective_payload_still_in_the_old_folder_holds_it() {
        var old       = Path.Combine(Npm().Root, "plugin");
        var installed = Path.Combine(Claude, "plugins", "installed_plugins.json");
        Write(Path.Combine(old, ".mcp.json"), "{}");
        Write(Path.Combine(BundlePlugin, ".mcp.json"), "{}");
        Write(Path.Combine(Claude, "settings.json"), Settings(BundlePlugin));

        Write(installed, $$$"""{"plugins":{"kcap@kcap":[{"scope":"user","installPath":"{{{old}}}"}]}}""");
        await Assert.That(await Holder()).IsEqualTo($"site:claude-plugin:{Claude}");

        Write(installed, $$$"""{"plugins":{"kcap@kcap":[{"scope":"user","installPath":"{{{BundlePlugin}}}"}]}}""");
        await Assert.That(await Holder()).IsNull();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Unreadable_Claude_plugin_records_hold_it() {
        Write(Path.Combine(Claude, "settings.json"), Settings(BundlePlugin));
        var installed = Path.Combine(Claude, "plugins", "installed_plugins.json");
        Write(installed, "{ not json");

        await Assert.That(await Holder()).IsEqualTo($"site:{installed}");
    }

    void Register(HarnessId vendor, string directory) =>
        Accounts.Mutate(r => (r with { Accounts = [.. r.Accounts, new VendorAccount(vendor + "-" + r.Accounts.Count, vendor, directory, "Work", DateTimeOffset.UtcNow)] }, 0));

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_registered_Claude_accounts_plugin_still_loading_the_old_folder_holds_it() {
        var work = Tmp.PathTo("work-claude");
        Register(HarnessId.Claude, work);
        Write(Path.Combine(Claude, "settings.json"), Settings(BundlePlugin));
        Write(Path.Combine(work, "settings.json"), Settings(Path.Combine(Npm().Root, "plugin")));

        await Assert.That(await Holder()).IsEqualTo($"site:{Path.Combine(work, "settings.json")}");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_registered_Codex_accounts_config_naming_the_old_binary_holds_it() {
        var work = Tmp.PathTo("work-codex");
        Register(HarnessId.Codex, work);
        Write(Path.Combine(Home.Path, ".codex", "config.toml"), "");
        var config = Path.Combine(work, "config.toml");
        Write(config, $"[mcp_servers.kcap-review]\ncommand = \"{Npm().CliBinary}\"\n");

        await Assert.That(await Holder()).IsEqualTo($"site:{config}");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task An_unreadable_account_registry_holds_it() {
        Write(Accounts.RegistryPath, "{ not json");

        await Assert.That(await Holder()).IsEqualTo($"site:accounts:{Accounts.RegistryPath}");
    }
}
