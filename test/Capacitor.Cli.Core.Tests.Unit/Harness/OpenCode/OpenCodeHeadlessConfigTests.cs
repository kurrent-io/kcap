using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Harness.OpenCode;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

public class OpenCodeHeadlessConfigTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string ConfigDir => Path.Combine(Tmp.Path, "config");
    string DataDir   => Path.Combine(Tmp.Path, "data");

    OpenCodePaths Paths => new(new(Tmp.Path), ConfigDir, null, DataDir);

    string Global(string name, string content) => Tmp.CreateFile(Path.Combine("config", name), content);

    OpenCodeHeadlessConfig Build(string? configFile = null, string? configContent = null) =>
        OpenCodeHeadlessConfig.Build(Paths, new Dictionary<string, string?> {
            ["OPENCODE_CONFIG"]         = configFile,
            ["OPENCODE_CONFIG_CONTENT"] = configContent,
        });

    static JsonObject Content(OpenCodeHeadlessConfig config) => JsonNode.Parse(config.ConfigContent)!.AsObject();

    static string? Model(OpenCodeHeadlessConfig config) => (string?)Content(config)["model"];

    [Test]
    public async Task The_model_is_taken_from_the_last_layer_that_sets_it() {
        Global("config.json", """{"model":"a/config"}""");
        await Assert.That(Model(Build())).IsEqualTo("a/config");

        Global("opencode.json", """{"model":"a/opencode-json"}""");
        await Assert.That(Model(Build())).IsEqualTo("a/opencode-json");

        Global("opencode.jsonc", """{"model":"a/opencode-jsonc"}""");
        await Assert.That(Model(Build())).IsEqualTo("a/opencode-jsonc");

        var file = Tmp.CreateFile("extra.json", """{"model":"a/file"}""");
        await Assert.That(Model(Build(file))).IsEqualTo("a/file");
        await Assert.That(Model(Build(file, """{"model":"a/content"}"""))).IsEqualTo("a/content");
        await Assert.That(Model(Build(file, """{"theme":"dark"}"""))).IsEqualTo("a/file");
    }

    [Test]
    public async Task Providers_are_merged_by_id_a_later_entry_replacing_an_earlier_one() {
        Global("opencode.json", """{"provider":{"a":{"options":{"x":1}},"b":{"options":{"y":1}}}}""");
        var file = Tmp.CreateFile("extra.json", """{"provider":{"b":{"options":{"z":2}}}}""");

        var provider = Content(Build(file, """{"provider":{"c":{"name":"C"}}}"""))["provider"]!;

        await Assert.That(JsonNode.DeepEquals(provider, JsonNode.Parse("""{"a":{"options":{"x":1}},"b":{"options":{"z":2}},"c":{"name":"C"}}"""))).IsTrue();
    }

    [Test]
    public async Task Comments_and_trailing_commas_are_accepted() {
        Global("opencode.jsonc", """
            {
              // the user's default
              "model": "a/m", /* inline */
              "provider": { "a": { "name": "A", }, },
            }
            """);

        var content = Content(Build());

        await Assert.That((string?)content["model"]).IsEqualTo("a/m");
        await Assert.That((string?)content["provider"]!["a"]!["name"]).IsEqualTo("A");
    }

    [Test]
    [Arguments("""{"model":""")]
    [Arguments("""["model"]""")]
    [Arguments("""{"model":42}""")]
    [Arguments("""{"provider":"a"}""")]
    [Arguments("""{"provider":{"a":"b"}}""")]
    public async Task A_malformed_file_layer_is_refused_naming_its_path(string text) {
        var path = Global("opencode.json", text);

        await Assert.That(() => Build()).Throws<HeadlessRunRefusedException>().WithMessageContaining(path);
    }

    [Test]
    public async Task A_malformed_inline_layer_is_refused_naming_its_variable() =>
        await Assert.That(() => Build(configContent: "{nope")).Throws<HeadlessRunRefusedException>().WithMessageContaining("OPENCODE_CONFIG_CONTENT");

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_file_layer_is_refused_naming_its_path() {
        var path = Tmp.CreateFile("extra.json", """{"model":"a/m"}""");
        File.SetUnixFileMode(path, UnixFileMode.None);
        Skip.When(Environment.UserName == "root", "root reads a mode-0 file");

        await Assert.That(() => Build(path)).Throws<HeadlessRunRefusedException>().WithMessageContaining(path);
    }

    [Test]
    public async Task A_config_file_variable_naming_nothing_is_no_layer() =>
        await Assert.That(Model(Build(Path.Combine(Tmp.Path, "missing.json")))).IsNull();

    [Test]
    public async Task With_no_layers_the_model_is_left_to_opencode_and_only_the_lockdown_is_written() {
        var content = Content(Build());

        await Assert.That(content.Select(p => p.Key)).IsEquivalentTo(["tools", "permission"]);
        await Assert.That(JsonNode.DeepEquals(content["tools"], JsonNode.Parse("""{"*":false}"""))).IsTrue();
    }

    [Test]
    public async Task Nothing_but_the_model_and_providers_is_copied_from_the_users_config() {
        Global("opencode.json", """
            {"model":"a/m","provider":{"a":{}},"mcp":{"x":{"type":"local","command":["x"]}},"instructions":["x.md"],
             "plugin":["p"],"agent":{"build":{"prompt":"x"}},"permission":{"bash":"allow"},"tools":{"bash":true}}
            """);

        var content = Content(Build(configContent: """{"permission":{"*":"allow"}}"""));

        await Assert.That(content.Select(p => p.Key)).IsEquivalentTo(["tools", "permission", "model", "provider"]);
        await Assert.That(JsonNode.DeepEquals(content["tools"], JsonNode.Parse("""{"*":false}"""))).IsTrue();
        await Assert.That(content["permission"]!.ToJsonString()).IsEqualTo(Build().Permission);
    }

    /// <summary>Written out rather than read from <see cref="OpenCodeForbiddenTools"/>, so a tool dropped from that
    /// list goes red here.</summary>
    [Test]
    public async Task The_permission_denies_everything_and_each_forbidden_tool_by_name() {
        var permission = JsonDocument.Parse(Build().Permission).RootElement;

        await Assert.That(permission.EnumerateObject().Select(p => p.Name)).IsEquivalentTo(
            ["*", "write", "edit", "patch", "bash", "webfetch", "websearch", "task", "skill", "todowrite"]);
        await Assert.That(permission.EnumerateObject().Select(p => p.Value.GetString() ?? "").Distinct()).IsEquivalentTo(["deny"]);
        await Assert.That(JsonNode.Parse(Build().ConfigContent)!["permission"]!.ToJsonString()).IsEqualTo(Build().Permission);
    }

    [Test]
    public async Task Without_an_auth_file_there_is_no_auth_content() =>
        await Assert.That(Build().AuthContent).IsNull();

    [Test]
    public async Task A_blank_auth_file_is_no_auth_content() {
        Tmp.CreateFile(Path.Combine("data", "opencode", "auth.json"), " \n");

        await Assert.That(Build().AuthContent).IsNull();
    }

    [Test]
    public async Task The_auth_file_is_passed_as_written() {
        const string auth = """{"anthropic":{"type":"api","key":"sk-test"}}""";
        Tmp.CreateFile(Path.Combine("data", "opencode", "auth.json"), auth);

        await Assert.That(Build().AuthContent).IsEqualTo(auth);
    }

    [Test]
    public async Task A_managed_config_file_is_found_by_name_in_the_managed_directory() {
        var dir = Tmp.CreateDir("managed").Path;
        await Assert.That(OpenCodeHeadlessConfig.ManagedConfigFile(dir)).IsNull();

        var file = Tmp.CreateFile(Path.Combine("managed", "opencode.jsonc"), "{}");
        await Assert.That(OpenCodeHeadlessConfig.ManagedConfigFile(dir)).IsEqualTo(file);
        await Assert.That(OpenCodeHeadlessConfig.ManagedConfigFile(Path.Combine(Tmp.Path, "absent"))).IsNull();
    }
}
