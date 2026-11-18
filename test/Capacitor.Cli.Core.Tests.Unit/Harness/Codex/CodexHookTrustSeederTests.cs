using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

[ParallelLimiter<SubprocessLimit>]
public class CodexHookTrustSeederTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    CodexPaths? _paths;

    // Resolved: the config writer refuses a symlinked component, and a Mac's temp root is one.
    CodexPaths Paths => _paths ??= new CodexPaths(Home, Tmp.GetResolvedPath("codex"));

    string KcapKey    => $"{Paths.UserHooksJson}:stop:0:1";
    string ForeignKey => $"{Paths.UserHooksJson}:stop:0:0";
    string Log        => Tmp.PathTo("requests.log");

    static string Hook(string key, string command, string status, string source, string sourcePath) =>
        new JsonObject {
            ["key"] = key, ["eventName"] = "stop", ["handlerType"] = "command", ["command"] = command,
            ["currentHash"] = "sha256:decl/sha256:content", ["trustStatus"] = status,
            ["source"] = source, ["sourcePath"] = sourcePath,
        }.ToJsonString();

    static string ListResult(params string[] hooks) =>
        $$"""{"data":[{"cwd":"/x","hooks":[{{string.Join(",", hooks)}}],"warnings":[],"errors":[]}]}""";

    string Listed(string status) => ListResult(
        Hook(ForeignKey, "echo mine", "untrusted", "user", Paths.UserHooksJson),
        Hook(KcapKey, "kcap hook --codex", status, "user", Paths.UserHooksJson));

    static IReadOnlyList<CodexHookTrustSeeder.ListedHook> KcapHooks(string result, string hooksJson) {
        using var doc = JsonDocument.Parse(result);
        return CodexHookTrustSeeder.KcapHooks(doc.RootElement, hooksJson);
    }

    /// <summary>Only kcap's own command hooks from this home's user hooks file are kcap's to trust: a user hook beside
    /// them, a project or plugin hook, another home's file, and a command that merely mentions kcap all stay out.</summary>
    [Test]
    public async Task Only_kcaps_hooks_from_this_homes_user_file_are_selected() {
        var hooks = Paths.UserHooksJson;
        var result = ListResult(
            Hook($"{hooks}:stop:0:0", "kcap hook --codex", "untrusted", "user", hooks),
            Hook($"{hooks}:stop:0:1", "echo mine", "untrusted", "user", hooks),
            Hook($"{hooks}:stop:0:2", "evil # kcap hook --codex", "untrusted", "user", hooks),
            Hook("/repo/.codex/hooks.json:stop:0:0", "kcap hook --codex", "untrusted", "project", "/repo/.codex/hooks.json"),
            Hook("plugin:stop:0:0", "kcap hook --codex", "untrusted", "plugin", hooks),
            Hook("/other/hooks.json:stop:0:0", "kcap hook --codex", "untrusted", "user", "/other/hooks.json"));

        var selected = KcapHooks(result, hooks);

        await Assert.That(selected.Select(h => h.Key).ToArray()).IsEquivalentTo([$"{hooks}:stop:0:0"]);
        await Assert.That(selected[0].CurrentHash).IsEqualTo("sha256:decl/sha256:content");
        await Assert.That(selected[0].TrustStatus).IsEqualTo("untrusted");
    }

    [Test]
    public async Task A_result_without_data_selects_nothing() {
        await Assert.That(KcapHooks("{}", Paths.UserHooksJson)).IsEmpty();
        await Assert.That(KcapHooks("[]", Paths.UserHooksJson)).IsEmpty();
    }

    /// <summary>A stub app-server. <c>hooks/list</c> answers <paramref name="before"/> until a <c>config/batchWrite</c>
    /// arrives, <paramref name="after"/> once one has; the write appends what Codex would persist to config.toml.</summary>
    CodexHookTrustSeeder Seeder(string before, string after) {
        var untrusted = Tmp.CreateFile("before.json", Response(before));
        var trusted   = Tmp.CreateFile("after.json", Response(after));
        var written   = Tmp.CreateFile("written.json", Response("""{"status":"ok","version":"v2","filePath":"/x","overriddenMetadata":null}"""));
        var toml      = Tmp.CreateFile("written.toml", $"""

            [hooks.state."{KcapKey}"]
            trusted_hash = "sha256:decl"
            trusted_content_hash = "sha256:content"

            """);
        var mark = Tmp.PathTo("written.mark");

        Tmp.CreateExecutable("bin/codex", $$$"""
            #!/bin/sh
            while IFS= read -r line; do
              printf '%s\n' "$line" >> '{{{Log}}}'
              id=${line#*'"id":'}; id=${id%%,*}
              case "$line" in
                *'"method":"initialize"'*) printf '%s\n' '{"jsonrpc":"2.0","id":1,"result":{"userAgent":"stub"}}' ;;
                *'"method":"hooks/list"'*)
                  if [ -f '{{{mark}}}' ]; then sed "s/__ID__/$id/" '{{{trusted}}}'; else sed "s/__ID__/$id/" '{{{untrusted}}}'; fi ;;
                *'"method":"config/batchWrite"'*)
                  cat '{{{toml}}}' >> '{{{Paths.ConfigToml}}}'; : > '{{{mark}}}'; sed "s/__ID__/$id/" '{{{written}}}' ;;
              esac
            done
            """);

        return new CodexHookTrustSeeder(TestHarnesses.Under(Home, BinaryProbe.Searching(Tmp.PathTo("bin"))), Home, TimeProvider.System) {
            Deadline = TimeSpan.FromSeconds(10),
        };
    }

    static string Response(string result) => $$"""{"jsonrpc":"2.0","id":__ID__,"result":{{result}}}""" + "\n";

    void InstallHooks() {
        Directory.CreateDirectory(Paths.Home);
        File.WriteAllText(Paths.ConfigToml, "model = \"gpt-5.5\"\n");
        CodexHooksWriter.Install(Paths.UserHooksJson);
    }

    /// <summary>The <c>codex exec</c> case: kcap's hook is untrusted, so kcap asks Codex to record trust for it, and
    /// for it alone, then claims what Codex wrote.</summary>
    [Test]
    public async Task Untrusted_kcap_hooks_are_trusted_through_codex_and_claimed() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        InstallHooks();

        var seed = await Seeder(Listed("untrusted"), Listed("trusted")).SeedAsync(Paths, CancellationToken.None);

        await Assert.That(seed).IsEqualTo(CodexHookTrustSeed.Trusted);

        var write = File.ReadAllLines(Log).Single(l => l.Contains("\"method\":\"config/batchWrite\""));
        using var request = JsonDocument.Parse(write);
        var edit  = request.RootElement.GetProperty("params").GetProperty("edits")[0];
        await Assert.That(edit.GetProperty("keyPath").GetString()).IsEqualTo("hooks.state");
        await Assert.That(edit.GetProperty("mergeStrategy").GetString()).IsEqualTo("upsert");
        var value = edit.GetProperty("value");
        await Assert.That(value.EnumerateObject().Select(p => p.Name).ToArray()).IsEquivalentTo([KcapKey]);
        await Assert.That(value.GetProperty(KcapKey).GetProperty("trusted_hash").GetString()).IsEqualTo("sha256:decl/sha256:content");

        var claims = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.Home, "mcp-ownership-v1.json")))!["hook_trust"]!.AsObject();
        await Assert.That(claims.Select(c => c.Key).ToArray()).IsEquivalentTo([KcapKey]);
        await Assert.That(claims[KcapKey]!["trusted_hash"]!.GetValue<string>()).IsEqualTo("sha256:decl");
    }

    [Test]
    public async Task Hooks_codex_already_trusts_are_not_written() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        InstallHooks();

        var seed = await Seeder(Listed("trusted"), Listed("trusted")).SeedAsync(Paths, CancellationToken.None);

        await Assert.That(seed).IsEqualTo(CodexHookTrustSeed.AlreadyTrusted);
        await Assert.That(File.ReadAllText(Log)).DoesNotContain("config/batchWrite");
        await Assert.That(CodexConfigToml.HasHookTrustClaim(Paths.ConfigToml)).IsFalse();
    }

    /// <summary>A hook kcap changed since it was trusted reads <c>modified</c>, and is re-trusted like a new one.</summary>
    [Test]
    public async Task A_modified_kcap_hook_is_trusted_again() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        InstallHooks();

        var seed = await Seeder(Listed("modified"), Listed("trusted")).SeedAsync(Paths, CancellationToken.None);

        await Assert.That(seed).IsEqualTo(CodexHookTrustSeed.Trusted);
        await Assert.That(File.ReadAllText(Log)).Contains("config/batchWrite");
    }

    /// <summary>Success is what Codex reports afterwards, not that the write was accepted.</summary>
    [Test]
    public async Task Hooks_codex_still_reports_untrusted_are_a_failure() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        InstallHooks();

        var seed = await Seeder(Listed("untrusted"), Listed("untrusted")).SeedAsync(Paths, CancellationToken.None);

        await Assert.That(seed).IsEqualTo(CodexHookTrustSeed.Failed);
    }

    [Test]
    public async Task Without_kcap_hooks_codex_is_not_started() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        var seed = await Seeder(Listed("untrusted"), Listed("trusted")).SeedAsync(Paths, CancellationToken.None);

        await Assert.That(seed).IsEqualTo(CodexHookTrustSeed.NoHooks);
        await Assert.That(File.Exists(Log)).IsFalse();
    }

    [Test]
    public async Task Missing_codex_is_unavailable() {
        InstallHooks();
        var seeder = new CodexHookTrustSeeder(TestHarnesses.Under(Home), Home, TimeProvider.System);

        await Assert.That(await seeder.SeedAsync(Paths, CancellationToken.None)).IsEqualTo(CodexHookTrustSeed.CodexUnavailable);
    }
}
