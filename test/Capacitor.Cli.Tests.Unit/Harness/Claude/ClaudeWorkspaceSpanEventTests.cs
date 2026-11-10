using Capacitor.Cli.Core.Workspace;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeWorkspaceSpanEventTests {
    const string Enter = """
        {"hook_event_name":"PostToolUse","session_id":"3fc0ace3-409c-4534-a7fb-e0bfecf274f7","cwd":"/work/a",
         "tool_name":"EnterWorktree","tool_input":{"name":"probe"},"tool_response":{"worktreePath":"/work/b","worktreeBranch":"worktree-probe"}}
        """;

    [Test]
    public async Task EnterWorktree_opens_on_the_destination_from_the_tool_response_never_the_cwd() {
        var evt = ClaudeWorkspaceSpanEvent.Parse(Enter);

        await Assert.That(evt).IsEqualTo(new ClaudeWorkspaceSpanEvent("post-tool-use", "3fc0ace3409c4534a7fbe0bfecf274f7", "/work/b", WorkspaceOpen.Join));
    }

    [Test]
    public async Task An_EnterWorktree_without_a_destination_opens_nothing() {
        await Assert.That(ClaudeWorkspaceSpanEvent.Parse(Enter.Replace("\"worktreePath\":\"/work/b\",", ""))).IsNull();
    }
}
