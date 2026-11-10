using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudePluginRecordsTests {
    const string Old = "/old/npm/kcap";
    const string New = "/Applications/kcap.app/Contents/Resources/kcap";

    [TempDir] public required TempDir Tmp { get; init; }

    string Markets   => Tmp.PathTo("plugins", "known_marketplaces.json");
    string Installed => Tmp.PathTo("plugins", "installed_plugins.json");

    static string Market(string name, string path) =>
        "\"" + name + "\":{\"source\":{\"source\":\"directory\",\"path\":\"" + path + "\"},\"installLocation\":\"" + path + "\",\"lastUpdated\":\"t\"}";

    [Test]
    public async Task The_kcap_directory_marketplace_follows_the_settings_source_path() {
        const string other = """{"source":{"source":"github","repo":"a/b"},"installLocation":"/x","lastUpdated":"t"}""";
        Tmp.CreateFile("plugins/known_marketplaces.json", "{" + Market("kcap", Old) + $$""","other":{{other}}}""");

        var result = ClaudePluginRecords.Repoint(Tmp.Path, New);

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var root = JsonNode.Parse(File.ReadAllText(Markets))!;
        await Assert.That(root["kcap"]!["source"]!["path"]!.GetValue<string>()).IsEqualTo(New);
        await Assert.That(root["kcap"]!["installLocation"]!.GetValue<string>()).IsEqualTo(New);
        await Assert.That(root["kcap"]!["lastUpdated"]!.GetValue<string>()).IsEqualTo("t");
        await Assert.That(root["other"]!.ToJsonString()).IsEqualTo(JsonNode.Parse(other)!.ToJsonString());
    }

    [Test]
    public async Task The_kcap_at_kcap_install_record_under_the_old_folder_moves_and_a_cache_path_stays() {
        Tmp.CreateFile("plugins/known_marketplaces.json", "{" + Market("kcap", Old) + "}");
        var cache = Tmp.PathTo("plugins", "cache", "kcap", "kcap", "0.6.2");
        Tmp.CreateFile("plugins/installed_plugins.json", $$$"""
            {"version":2,"plugins":{
              "kcap@kcap":[{"scope":"user","installPath":"{{{Old}}}","version":"1"},{"scope":"project","installPath":{{{JsonValue.Create(cache).ToJsonString()}}}}],
              "other@x":[{"installPath":"{{{Old}}}"}]}}
            """);

        var result = ClaudePluginRecords.Repoint(Tmp.Path, New);

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var plugins = JsonNode.Parse(File.ReadAllText(Installed))!["plugins"]!;
        await Assert.That(plugins["kcap@kcap"]![0]!["installPath"]!.GetValue<string>()).IsEqualTo(New);
        await Assert.That(plugins["kcap@kcap"]![0]!["version"]!.GetValue<string>()).IsEqualTo("1");
        await Assert.That(plugins["kcap@kcap"]![1]!["installPath"]!.GetValue<string>()).IsEqualTo(cache);
        await Assert.That(plugins["other@x"]![0]!["installPath"]!.GetValue<string>()).IsEqualTo(Old);
    }

    [Test]
    public async Task Legacy_kurrent_and_kapacitor_marketplace_keys_are_repointed() {
        Tmp.CreateFile("plugins/known_marketplaces.json", "{" + Market("kurrent", Old) + "," + Market("kapacitor", Old) + "}");

        var result = ClaudePluginRecords.Repoint(Tmp.Path, New);

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var root = JsonNode.Parse(File.ReadAllText(Markets))!;
        await Assert.That(root["kurrent"]!["installLocation"]!.GetValue<string>()).IsEqualTo(New);
        await Assert.That(root["kapacitor"]!["source"]!["path"]!.GetValue<string>()).IsEqualTo(New);
    }

    /// <summary>The marketplace names the folder through a link, as an nvm <c>current</c> path does, and the record
    /// names the resolved folder; a child whose name starts with two dots is still a child.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_record_under_another_spelling_of_the_old_folder_moves_with_its_suffix() {
        Tmp.CreateDir("v22", "kcap", "..cache");
        File.CreateSymbolicLink(Tmp.PathTo("current"), Tmp.PathTo("v22"));
        var record = Path.Combine(Tmp.GetResolvedPath(Path.Combine("v22", "kcap")), "..cache");
        Tmp.CreateFile("plugins/known_marketplaces.json", "{" + Market("kcap", Tmp.PathTo("current", "kcap")) + "}");
        Tmp.CreateFile("plugins/installed_plugins.json",
            $$$"""{"version":2,"plugins":{"kcap@kcap":[{"scope":"user","installPath":{{{JsonValue.Create(record).ToJsonString()}}}}]}}""");

        var result = ClaudePluginRecords.Repoint(Tmp.Path, New);

        await Assert.That(result).IsEqualTo(SettingsEdit.Changed);
        var plugins = JsonNode.Parse(File.ReadAllText(Installed))!["plugins"]!;
        await Assert.That(plugins["kcap@kcap"]![0]!["installPath"]!.GetValue<string>()).IsEqualTo(Path.Combine(New, "..cache"));
    }

    [Test]
    public async Task A_home_without_plugin_records_is_unchanged_and_creates_no_file() {
        var result = ClaudePluginRecords.Repoint(Tmp.Path, New);

        await Assert.That(result).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(Directory.Exists(Tmp.PathTo("plugins"))).IsFalse();
    }

    [Test]
    public async Task Malformed_records_fail_without_writing() {
        Tmp.CreateFile("plugins/known_marketplaces.json", "{" + Market("kcap", Old) + "}");
        Tmp.CreateFile("plugins/installed_plugins.json", "{ broken");
        var before = File.ReadAllText(Markets);

        var result = ClaudePluginRecords.Repoint(Tmp.Path, New);

        await Assert.That(result).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(Installed)).IsEqualTo("{ broken");
        await Assert.That(File.ReadAllText(Markets)).IsEqualTo(before);
    }

    [Test]
    public async Task Malformed_records_with_no_kcap_marketplace_are_never_read() {
        Tmp.CreateFile("plugins/known_marketplaces.json", "{\"other\":{\"source\":{\"source\":\"github\",\"repo\":\"o/r\"}}}");
        Tmp.CreateFile("plugins/installed_plugins.json", "{ broken");

        var result = ClaudePluginRecords.Repoint(Tmp.Path, New);

        await Assert.That(result).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(File.ReadAllText(Installed)).IsEqualTo("{ broken");
    }

    [Test]
    public async Task A_null_claude_home_never_touches_records() {
        Tmp.CreateFile("plugins/known_marketplaces.json", "{" + Market("kcap", Old) + "}");
        var before = File.ReadAllText(Markets);

        ClaudePluginWriter.Install(Tmp.PathTo("settings.json"), null, New);

        await Assert.That(File.ReadAllText(Markets)).IsEqualTo(before);
    }
}
