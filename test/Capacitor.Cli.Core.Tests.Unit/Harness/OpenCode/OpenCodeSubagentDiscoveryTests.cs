using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

/// <summary>
/// Unit tests for <see cref="OpenCodeSubagentDiscovery"/>: the nested
/// child-file convention the kcap plugin writes + the parent watcher scans, the agent-name
/// resolution, and the vendor-agnostic <c>/hooks/subagent-{start,stop}</c> payload shapes
/// (whose required-field set the server's HookBase enforces).
/// </summary>
public class OpenCodeSubagentDiscoveryTests {
    [Test]
    public async Task SubagentDir_IsNestedDirNamedAfterParent() {
        var dir = OpenCodeSubagentDiscovery.SubagentDir(
            Path.Combine("cache", "kcap", "opencode", "ses_parent.jsonl"));
        await Assert.That(dir).IsEqualTo(Path.Combine("cache", "kcap", "opencode", "ses_parent"));
    }

    [Test]
    public async Task EnumerateSubagentFiles_ReturnsChildFiles_ElseEmpty() {
        using var tmp = new TempDir();

        var lonely = tmp.CreateFile("ses_lonely.jsonl", "");
        await Assert.That(OpenCodeSubagentDiscovery.EnumerateSubagentFiles(lonely).Count).IsEqualTo(0);

        var parent = tmp.CreateFile("ses_parent.jsonl", "");
        var nested = tmp.CreateDir("ses_parent");
        nested.CreateFile("ses_child1.jsonl", "");
        nested.CreateFile("ses_child2.jsonl", "");

        await Assert.That(OpenCodeSubagentDiscovery.EnumerateSubagentFiles(parent).Count).IsEqualTo(2);
    }

    [Test]
    public async Task ResolveAgentType_ReadsInfoAgent_ElseFallsBackToSubagent() {
        using var tmp = new TempDir();

        var withAgent = tmp.PathTo("c1.jsonl");
        File.WriteAllText(withAgent,
            "{\"info\":{\"role\":\"user\",\"id\":\"m1\"},\"parts\":[]}\n" +
            "{\"info\":{\"role\":\"assistant\",\"id\":\"m2\",\"agent\":\"general\"},\"parts\":[]}\n");
        await Assert.That(OpenCodeSubagentDiscovery.ResolveAgentType(withAgent)).IsEqualTo("general");

        var noAgent = tmp.CreateFile("c2.jsonl", "{\"info\":{\"role\":\"user\",\"id\":\"m1\"},\"parts\":[]}\n");
        await Assert.That(OpenCodeSubagentDiscovery.ResolveAgentType(noAgent)).IsEqualTo("subagent");
    }

}
