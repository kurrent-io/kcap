using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SessionImporterScanTests {
    [Test]
    public async Task ScanAgentLifecycle_ExtractsSubagentTypeFromAsyncLaunchedInvocation() {
        const string transcript = """
                         {"type":"user","message":{"content":"go"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{"subagent_type":"code-reviewer","prompt":"review this"}}]}}
                         {"type":"result","tool_use_id":"toolu_1","tool_result":{"status":"async_launched","agentId":"agent-abc"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.tmp", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.FirstLineByAgent.ContainsKey("agent-abc")).IsTrue();
        await Assert.That(scan.AgentTypeByAgent["agent-abc"]).IsEqualTo("code-reviewer");
    }

    [Test]
    public async Task ScanAgentLifecycle_ExtractsSubagentTypeFromForegroundToolUseResult() {
        const string transcript = """
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Task","input":{"subagent_type":"general-purpose"}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_2"}]},"toolUseResult":{"agentId":"agent-xyz"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.tmp", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.AgentTypeByAgent["agent-xyz"]).IsEqualTo("general-purpose");
    }

    [Test]
    public async Task ScanAgentLifecycle_AgentSeenFirstInAgentProgress_StillResolvesTypeFromLaterInvocation() {
        // When agent_progress for an agentId arrives before the assistant tool_use +
        // async_launched pair, an early-return on FirstLineByAgent.ContainsKey must not
        // stop subagent_type being copied into AgentTypeByAgent.
        const string transcript = """
                         {"type":"progress","data":{"type":"agent_progress","agentId":"agent-abc"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{"subagent_type":"code-reviewer"}}]}}
                         {"type":"result","tool_use_id":"toolu_1","tool_result":{"status":"async_launched","agentId":"agent-abc"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.tmp", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.FirstLineByAgent["agent-abc"]).IsEqualTo(0); // progress line wins the position
        await Assert.That(scan.AgentTypeByAgent["agent-abc"]).IsEqualTo("code-reviewer");
    }

    [Test]
    public async Task ScanAgentLifecycle_AgentSeenFirstInAgentProgress_StillResolvesTypeFromForegroundToolUseResult() {
        // Same regression as above, but for the foreground toolUseResult path.
        const string transcript = """
                         {"type":"progress","data":{"type":"agent_progress","agentId":"agent-xyz"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Task","input":{"subagent_type":"general-purpose"}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_2"}]},"toolUseResult":{"agentId":"agent-xyz"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.tmp", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.FirstLineByAgent["agent-xyz"]).IsEqualTo(0);
        await Assert.That(scan.AgentTypeByAgent["agent-xyz"]).IsEqualTo("general-purpose");
    }

    [Test]
    public async Task ScanAgentLifecycle_AgentWithoutInvocation_HasNoType() {
        // progress-only reference — no parent tool_use, so the subagent_type is unknown.
        const string transcript = """
                         {"type":"progress","data":{"type":"agent_progress","agentId":"agent-orphan"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.tmp", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.FirstLineByAgent.ContainsKey("agent-orphan")).IsTrue();
        await Assert.That(scan.AgentTypeByAgent.ContainsKey("agent-orphan")).IsFalse();
    }

    /// <summary>A parent whose result never names its agent: the meta file beside the subagent's transcript
    /// is what ties the two, which seats the agent at its call and names the call on its start.</summary>
    [Test]
    public async Task ScanAgentLifecycle_LinksAnAgentToItsCallThroughItsMetaFile() {
        const string transcript = """
                         {"type":"user","message":{"content":"go"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{"subagent_type":"Explore","prompt":"map it"}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1"}]},"toolUseResult":{"totalDurationMs":1000}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.jsonl", transcript + "\n");
        tmp.CreateFile(["transcript", "subagents", "agent-a1.jsonl"], "{}");
        tmp.CreateFile(["transcript", "subagents", "agent-a1.meta.json"], """{"toolUseId":"toolu_1"}""");

        var scan = SessionImporter.ScanAgentLifecycle(path, resumeFromLine: 2);

        await Assert.That(scan.SpawnCallByAgent["a1"]).IsEqualTo("toolu_1");
        await Assert.That(scan.FirstLineByAgent["a1"]).IsEqualTo(1);
        await Assert.That(scan.AgentTypeByAgent["a1"]).IsEqualTo("Explore");
        await Assert.That(scan.ResumeLineByAgent["a1"]).IsEqualTo(2);
    }

    [Test]
    [Arguments(1, true)]
    [Arguments(2, false)]
    public async Task ScanAgentLifecycle_ResumesAForegroundAgentAtItsUnsentResult(int startLine, bool replay) {
        const string transcript = """
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Agent","input":{"subagent_type":"general-purpose"}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_2"}]},"toolUseResult":{"status":"completed","agentId":"agent-xyz"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.jsonl", transcript + "\n");

        var scan = SessionImporter.ScanAgentLifecycle(path, resumeFromLine: startLine);

        await Assert.That(scan.SpawnCallByAgent["agent-xyz"]).IsEqualTo("toolu_2");
        await Assert.That(scan.FirstLineByAgent["agent-xyz"]).IsEqualTo(0);
        await Assert.That(scan.ResumeLineByAgent.ContainsKey("agent-xyz")).IsEqualTo(replay);
        if (replay) await Assert.That(scan.ResumeLineByAgent["agent-xyz"]).IsEqualTo(1);
    }

    [Test]
    [Arguments(1, 1)]
    [Arguments(2, 2)]
    public async Task A_linked_invocation_precedes_progress_when_both_are_unsent(int startLine, int expected) {
        const string transcript = """
                                  {"type":"user","message":{"content":"go"}}
                                  {"type":"assistant","message":{"content":[{"type":"tool_use","id":"call","name":"Agent","input":{}}]}}
                                  {"type":"progress","data":{"type":"agent_progress","agentId":"agent-xyz"}}
                                  {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"call"}]},"toolUseResult":{"agentId":"agent-xyz"}}
                                  """;
        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.jsonl", transcript + "\n");

        var scan = SessionImporter.ScanAgentLifecycle(path, resumeFromLine: startLine);

        await Assert.That(scan.FirstLineByAgent["agent-xyz"]).IsEqualTo(2);
        await Assert.That(scan.ResumeLineByAgent["agent-xyz"]).IsEqualTo(expected);
    }

    /// <summary>Evidence that disagrees, even where one side names a call this transcript does not hold, or
    /// one call claimed by two agents, names no call at all: a wrong link would open another subagent's
    /// transcript from the row.</summary>
    [Test]
    public async Task ScanAgentLifecycle_LeavesConflictingOrSharedCallsUnlinked() {
        const string transcript = """
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{}}]}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Task","input":{}}]}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_3","name":"Task","input":{}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1"}]},"toolUseResult":{"status":"completed","agentId":"a1"}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_3"}]},"toolUseResult":{"status":"completed","agentId":"e1"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.jsonl", transcript);
        tmp.CreateFile(["transcript", "subagents", "agent-a1.meta.json"], """{"toolUseId":"toolu_2"}""");
        tmp.CreateFile(["transcript", "subagents", "agent-b1.meta.json"], """{"toolUseId":"toolu_9"}""");
        tmp.CreateFile(["transcript", "subagents", "agent-c1.meta.json"], """{"toolUseId":"toolu_2"}""");
        tmp.CreateFile(["transcript", "subagents", "agent-d1.meta.json"], """{"toolUseId":"toolu_2"}""");
        tmp.CreateFile(["transcript", "subagents", "agent-e1.meta.json"], """{"toolUseId":"toolu_8"}""");

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.SpawnCallByAgent).IsEmpty();
        await Assert.That(scan.FirstLineByAgent.ContainsKey("c1")).IsFalse();
    }

    [Test]
    [Arguments(1, 1)]
    [Arguments(2, 3)]
    public async Task ScanAgentLifecycle_CountsATaskNotificationAsAReferenceToTheBackgroundAgent(int startLine, int expected) {
        var transcript = """
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_9","name":"Agent","input":{"subagent_type":"Explore","run_in_background":true}}]}}
                         {"type":"result","tool_use_id":"toolu_9","tool_result":{"status":"async_launched","agentId":"bg1"}}
                         {"type":"user","message":{"content":"go on"}}
                         {"type":"user","message":{"content":"<task-notification>\n<task-id>bg1</task-id>\n<tool-use-id>toolu_9</tool-use-id>\n<status>completed</status>\n</task-notification>"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.jsonl", transcript + "\n");

        var scan = SessionImporter.ScanAgentLifecycle(path, resumeFromLine: startLine);

        await Assert.That(scan.FirstLineByAgent["bg1"]).IsEqualTo(0);
        await Assert.That(scan.ResumeLineByAgent["bg1"]).IsEqualTo(expected);
    }
}
