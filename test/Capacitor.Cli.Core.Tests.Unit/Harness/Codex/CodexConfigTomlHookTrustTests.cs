using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Codex;
using Tomlyn;
using Tomlyn.Model;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexConfigTomlHookTrustTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Stop  = "/u/.codex/hooks.json:stop:0:0";
    const string Start = "/u/.codex/hooks.json:session_start:0:0";
    const string Users = "/u/.codex/hooks.json:stop:0:1";

    string Config => Tmp.GetResolvedPath("config.toml");
    string Ledger => Tmp.GetResolvedPath("mcp-ownership-v1.json");

    static HashSet<string> Keys(params string[] keys) => new(keys, StringComparer.Ordinal);

    TomlTable ReadToml() => TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(Config))!;

    TomlTable? State(string key) =>
        ReadToml().TryGetValue("hooks", out var hooks) && ((TomlTable)hooks).TryGetValue("state", out var state)
     && ((TomlTable)state).TryGetValue(key, out var entry)
            ? (TomlTable)entry
            : null;

    JsonObject? Claims() => JsonNode.Parse(File.ReadAllText(Ledger))!["hook_trust"] as JsonObject;

    /// <summary>Stands in for Codex's <c>config/batchWrite</c>: appends what Codex persists for one approved hook.</summary>
    Func<Task> CodexWrites(params string[] keys) => () => {
        var toml = string.Concat(keys.Select(k => $"\n[hooks.state.\"{k}\"]\ntrusted_hash = \"sha256:d-{k.Length}\"\ntrusted_content_hash = \"sha256:c-{k.Length}\"\n"));
        File.AppendAllText(Config, toml);
        return Task.CompletedTask;
    };

    [Test]
    public async Task What_codex_wrote_is_claimed_and_uninstall_removes_exactly_that() {
        File.WriteAllText(Config, "model = \"gpt-5.5\"\n");

        await CodexConfigToml.TrustHooksAsync(Config, Keys(Stop), Keys(Stop), CodexWrites(Stop));

        await Assert.That(Claims()![Stop]!["trusted_hash"]!.GetValue<string>()).IsEqualTo((string)State(Stop)!["trusted_hash"]);
        await Assert.That(Claims()![Stop]!["trusted_content_hash"]!.GetValue<string>()).IsEqualTo((string)State(Stop)!["trusted_content_hash"]);

        await Assert.That(CodexConfigToml.ReleaseHookTrust(Config)).IsEqualTo(CodexConfigToml.Change.Updated);

        var root = ReadToml();
        await Assert.That(root.Keys.ToArray()).IsEquivalentTo(["model"]);
        await Assert.That(Claims()).IsNull();
    }

    /// <summary>Trust kcap never wrote, and trust the user re-approved after kcap wrote it, are not kcap's to remove;
    /// a field beside the hashes on a claimed entry stays too.</summary>
    [Test]
    public async Task Uninstall_keeps_trust_it_cannot_prove_it_wrote() {
        File.WriteAllText(Config, $"""
            [hooks.state."{Users}"]
            trusted_hash = "sha256:theirs"
            """);
        await CodexConfigToml.TrustHooksAsync(Config, Keys(Stop, Start), Keys(Stop, Start), CodexWrites(Stop, Start));

        var root   = ReadToml();
        var states = (TomlTable)((TomlTable)root["hooks"])["state"];
        ((TomlTable)states[Start])["trusted_hash"] = "sha256:reapproved";
        ((TomlTable)states[Stop])["enabled"]       = false;
        File.WriteAllText(Config, TomlSerializer.Serialize(root));

        var change = CodexConfigToml.ReleaseHookTrust(Config);

        await Assert.That(change).IsEqualTo(CodexConfigToml.Change.UpdatedWithPreservedEntries);
        await Assert.That((string)State(Users)!["trusted_hash"]).IsEqualTo("sha256:theirs");
        await Assert.That((string)State(Start)!["trusted_hash"]).IsEqualTo("sha256:reapproved");
        await Assert.That(State(Stop)!.Keys.ToArray()).IsEquivalentTo(["enabled"]);
        await Assert.That(Claims()).IsNull();
    }

    /// <summary>A key kcap trusted that no longer names a kcap hook (its index moved) is cleaned up on the next
    /// install; the key that still names one keeps its claim.</summary>
    [Test]
    public async Task A_claimed_key_that_is_no_longer_kcaps_is_removed_on_the_next_install() {
        File.WriteAllText(Config, "");
        await CodexConfigToml.TrustHooksAsync(Config, Keys(Stop, Start), Keys(Stop, Start), CodexWrites(Stop, Start));

        await CodexConfigToml.TrustHooksAsync(Config, Keys(), Keys(Start), null);

        await Assert.That(State(Stop)).IsNull();
        await Assert.That(State(Start)).IsNotNull();
        await Assert.That(Claims()!.Select(c => c.Key).ToArray()).IsEquivalentTo([Start]);
    }

    [Test]
    public async Task A_repeat_install_with_nothing_to_trust_changes_no_config() {
        File.WriteAllText(Config, "model = \"gpt-5.5\"\n");
        await CodexConfigToml.TrustHooksAsync(Config, Keys(Stop), Keys(Stop), CodexWrites(Stop));
        var before = File.ReadAllText(Config);

        await CodexConfigToml.TrustHooksAsync(Config, Keys(), Keys(Stop), null);

        await Assert.That(File.ReadAllText(Config)).IsEqualTo(before);
        await Assert.That(Claims()!.ContainsKey(Stop)).IsTrue();
        await Assert.That(CodexConfigToml.HasHookTrustClaim(Config)).IsTrue();
    }

    /// <summary>An unreadable ledger may be the only record of what kcap wrote, so Codex is not asked to write
    /// anything that could not be claimed.</summary>
    [Test]
    public async Task An_unreadable_ledger_stops_the_write() {
        File.WriteAllText(Config, "");
        File.WriteAllText(Ledger, "{ not json");
        var asked = false;

        await Assert.That(async () => await CodexConfigToml.TrustHooksAsync(Config, Keys(Stop), Keys(Stop), () => {
            asked = true;
            return Task.CompletedTask;
        })).Throws<InvalidDataException>();

        await Assert.That(asked).IsFalse();
        await Assert.That(File.ReadAllText(Ledger)).IsEqualTo("{ not json");
    }

    [Test]
    public async Task Uninstall_without_a_claim_touches_nothing() {
        File.WriteAllText(Config, $"""
            [hooks.state."{Stop}"]
            trusted_hash = "sha256:theirs"
            """);
        var before = File.ReadAllText(Config);

        await Assert.That(CodexConfigToml.ReleaseHookTrust(Config)).IsEqualTo(CodexConfigToml.Change.Unchanged);
        await Assert.That(File.ReadAllText(Config)).IsEqualTo(before);
        await Assert.That(CodexConfigToml.HasHookTrustClaim(Config)).IsFalse();
    }
}
