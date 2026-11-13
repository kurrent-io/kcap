using System.Text.Json;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

/// <summary>Pins the <c>skills/list</c> mapping: flat or per-cwd skills become commands, a disabled one is
/// left out, and an unexpected shape yields an empty list rather than throwing.</summary>
public class CodexSkillsTests {
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Extract_reads_a_flat_data_array() {
        var result = Parse("""{ "data": [ { "name": "review", "description": "Review a PR" }, { "name": "plan" } ] }""");

        var commands = CodexSkills.Extract(result);

        await Assert.That(commands.Count).IsEqualTo(2);
        await Assert.That(commands[0]).IsEqualTo(new HostedAgentCommand("review", "Review a PR", null));
        await Assert.That(commands[1]).IsEqualTo(new HostedAgentCommand("plan", null, null));
    }

    [Test]
    public async Task Extract_reads_a_grouped_shape_with_nested_skills() {
        var result = Parse("""{ "data": [ { "skills": [ { "name": "a" }, { "name": "b", "description": "B" } ] } ] }""");

        var commands = CodexSkills.Extract(result);

        await Assert.That(commands.Count).IsEqualTo(2);
        await Assert.That(commands[1]).IsEqualTo(new HostedAgentCommand("b", "B", null));
    }

    [Test]
    public async Task Extract_skips_a_disabled_skill() {
        var result = Parse("""{ "data": [ { "skills": [ { "name": "on", "enabled": true }, { "name": "off", "enabled": false } ] } ] }""");

        await Assert.That(CodexSkills.Extract(result).Select(c => c.Name)).IsEquivalentTo(["on"]);
    }

    [Test]
    public async Task Extract_returns_empty_for_an_unexpected_shape() {
        await Assert.That(CodexSkills.Extract(Parse("""{ "somethingElse": 1 }"""))).IsEmpty();
        await Assert.That(CodexSkills.Extract(Parse("""{ "data": [] }"""))).IsEmpty();
    }
}
