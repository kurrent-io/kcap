using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudePluginWriterTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Install_enables_the_plugin_and_registers_the_marketplace() {
        var settings = Tmp.PathTo("settings.json");

        var result = ClaudePluginWriter.Install(settings, "/opt/kcap/plugin");

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var root = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(root["enabledPlugins"]!["kcap@kcap"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>()).IsEqualTo("/opt/kcap/plugin");
        await Assert.That(ClaudePluginInstaller.IsInstalled(settings)).IsTrue();
    }

    [Test]
    public async Task Install_removes_legacy_entries_and_keeps_user_settings() {
        var settings = Tmp.CreateFile("settings.json", """
            { "model": "opus", "enabledPlugins": { "kapacitor@kapacitor": true, "other@x": true },
              "extraKnownMarketplaces": { "kurrent": {} } }
            """);

        ClaudePluginWriter.Install(settings, "/p");

        var root = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(root["model"]!.GetValue<string>()).IsEqualTo("opus");
        await Assert.That(root["enabledPlugins"]!["other@x"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["enabledPlugins"]!["kapacitor@kapacitor"]).IsNull();
        await Assert.That(root["extraKnownMarketplaces"]!["kurrent"]).IsNull();
    }

    [Test]
    public async Task Install_refuses_a_malformed_settings_file() {
        var settings = Tmp.CreateFile("settings.json", "{ broken");

        await Assert.That(ClaudePluginWriter.Install(settings, "/p")).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(settings)).IsEqualTo("{ broken");
    }

    [Test]
    public async Task Remove_drops_only_kcap_entries() {
        var settings = Tmp.PathTo("settings.json");
        ClaudePluginWriter.Install(settings, "/p");
        var root = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        root["enabledPlugins"]!["other@x"] = true;
        File.WriteAllText(settings, root.ToJsonString());

        var result = ClaudePluginWriter.Remove(settings);

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var after = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(after["enabledPlugins"]!["kcap@kcap"]).IsNull();
        await Assert.That(after["enabledPlugins"]!["other@x"]!.GetValue<bool>()).IsTrue();
        await Assert.That(ClaudePluginInstaller.ReadMarker(settings)).IsNull();
    }

    [Test]
    public async Task Remove_on_a_missing_file_is_unchanged() {
        await Assert.That(ClaudePluginWriter.Remove(Tmp.PathTo("nope.json"))).IsEqualTo(SettingsEdit.Unchanged);
    }

    [Test]
    public async Task Install_repairs_a_non_bool_plugin_flag() {
        var settings = Tmp.CreateFile("settings.json", """{"enabledPlugins":{"kcap@kcap":"yes"}}""");

        var result = ClaudePluginWriter.Install(settings, "/p");

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(JsonNode.Parse(File.ReadAllText(settings))!["enabledPlugins"]!["kcap@kcap"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Install_repairs_a_malformed_marketplace_entry() {
        var settings = Tmp.CreateFile("settings.json", """{"extraKnownMarketplaces":{"kcap":{"source":"x"}}}""");

        var result = ClaudePluginWriter.Install(settings, "/p");

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var path = JsonNode.Parse(File.ReadAllText(settings))!["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>();
        await Assert.That(path).IsEqualTo("/p");
    }

    [Test]
    public async Task Repeated_install_is_unchanged_and_still_writes_the_marker() {
        var settings = Tmp.PathTo("settings.json");
        ClaudePluginWriter.Install(settings, "/p");
        ClaudePluginInstaller.DeleteMarker(settings);

        var result = ClaudePluginWriter.Install(settings, "/p");

        await Assert.That(result).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(ClaudePluginInstaller.ReadMarker(settings)).IsNotNull();
    }

    /// <summary>The kcap plugin's install owns the marketplace entry, so the mod goes in beside it without moving it.</summary>
    [Test]
    public async Task Install_steering_enables_the_mod_in_the_kcap_marketplace_already_registered() {
        var settings = Tmp.PathTo("settings.json");
        ClaudePluginWriter.Install(settings, "/registered");

        var result = ClaudePluginWriter.InstallSteering(settings);

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var root = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(root["enabledPlugins"]!["kcap-steering@kcap"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["enabledPlugins"]!["kcap@kcap"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>()).IsEqualTo("/registered");
        await Assert.That(ClaudePluginInstaller.HasInstalledSteering(settings)).IsTrue();
        await Assert.That(ClaudePluginInstaller.IsSteeringEnabled(settings)).IsTrue();
    }

    /// <summary>Only kcap's own install registers its marketplace: an entry written here would read as kcap installed,
    /// and the next <c>--if-installed</c> refresh would turn recording on.</summary>
    [Test]
    [Arguments("""{ "model": "opus" }""")]
    [Arguments("""{ "extraKnownMarketplaces": { "other": {} }, "enabledPlugins": { "other@other": true } }""")]
    [Arguments("""{ "extraKnownMarketplaces": "kcap" }""")]
    [Arguments("")]
    public async Task Install_steering_writes_nothing_where_the_kcap_marketplace_is_not_registered(string content) {
        var settings = Tmp.CreateFile("settings.json", content);

        await Assert.That(ClaudePluginWriter.InstallSteering(settings)).IsNull();
        await Assert.That(File.ReadAllText(settings)).IsEqualTo(content);
        await Assert.That(ClaudePluginInstaller.HasInstalledSteering(settings)).IsFalse();
        await Assert.That(ClaudePluginInstaller.IsInstalled(settings)).IsFalse();
    }

    [Test]
    public async Task Install_steering_creates_no_settings_file() {
        var settings = Tmp.PathTo("claude", "settings.json");

        await Assert.That(ClaudePluginWriter.InstallSteering(settings)).IsNull();
        await Assert.That(Directory.Exists(Tmp.PathTo("claude"))).IsFalse();
    }

    /// <summary>Claude records a disabled plugin as <c>false</c>; that is the person's answer, not a gap to fill.</summary>
    [Test]
    public async Task Install_steering_leaves_a_choice_already_in_the_file() {
        var settings = Tmp.CreateFile("settings.json", """
            { "enabledPlugins": { "kcap-steering@kcap": false },
              "extraKnownMarketplaces": { "kcap": { "source": { "source": "directory", "path": "/p" } } } }
            """);
        var before = File.ReadAllText(settings);

        var result = ClaudePluginWriter.InstallSteering(settings);

        await Assert.That(result).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(File.ReadAllText(settings)).IsEqualTo(before);
        await Assert.That(ClaudePluginInstaller.HasInstalledSteering(settings)).IsTrue();
        await Assert.That(ClaudePluginInstaller.IsSteeringEnabled(settings)).IsFalse();
    }

    [Test]
    [Arguments("{ broken")]
    [Arguments("""{ "extraKnownMarketplaces": { "kcap": {} }, "enabledPlugins": [] }""")]
    [Arguments("""{ "extraKnownMarketplaces": { "kcap": {} }, "enabledPlugins": "kcap-steering@kcap" }""")]
    public async Task Install_steering_refuses_a_settings_file_it_cannot_read(string content) {
        var settings = Tmp.CreateFile("settings.json", content);

        await Assert.That(ClaudePluginWriter.InstallSteering(settings)).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(settings)).IsEqualTo(content);
        await Assert.That(ClaudePluginInstaller.HasInstalledSteering(settings)).IsFalse();
    }

    /// <summary>The mod lives in kcap's marketplace, so an entry left behind would name a marketplace that is gone.</summary>
    [Test]
    public async Task Remove_takes_the_steering_mod_out_with_its_marketplace() {
        var settings = Tmp.PathTo("settings.json");
        ClaudePluginWriter.Install(settings, "/p");
        ClaudePluginWriter.InstallSteering(settings);

        var result = ClaudePluginWriter.Remove(settings);

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var root = JsonNode.Parse(File.ReadAllText(settings))!;
        await Assert.That(root["enabledPlugins"]!["kcap-steering@kcap"]).IsNull();
        await Assert.That(ClaudePluginInstaller.HasInstalledSteering(settings)).IsFalse();
    }
}
