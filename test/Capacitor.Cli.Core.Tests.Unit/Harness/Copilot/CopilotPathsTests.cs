using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Copilot;

public class CopilotPathsTests {
    static CopilotPaths Under(string home, string? copilotHome = null) => new(new(home), copilotHome);

    [Test]
    public async Task Files_resolve_under_the_copilot_root() {
        var paths = Under("/h");

        await Assert.That(paths.McpConfigJson).IsEqualTo(Path.Combine("/h", ".copilot", "mcp-config.json"));
        await Assert.That(paths.InstructionsMd)
            .IsEqualTo(Path.Combine("/h", ".copilot", "copilot-instructions.md"));
        await Assert.That(paths.KcapHooksJson).IsEqualTo(Path.Combine("/h", ".copilot", "hooks", "kcap.json"));
    }

    [Test]
    public async Task Copilot_home_override_replaces_the_whole_root() {
        // $COPILOT_HOME replaces the entire ~/.copilot path, so the files sit directly under it.
        var paths = Under("/h", "/custom/loc");

        await Assert.That(paths.McpConfigJson).IsEqualTo(Path.Combine("/custom/loc", "mcp-config.json"));
        await Assert.That(paths.KcapHooksJson).IsEqualTo(Path.Combine("/custom/loc", "hooks", "kcap.json"));
    }

    [Test]
    public async Task A_local_transcripts_chat_log_sits_in_its_workspaces_chat_sessions() {
        // A native root: on Windows GetDirectoryName rewrites a leading '/' to a backslash
        var workspace  = Path.Combine(Path.GetFullPath("/ws"), "abc");
        var transcript = Path.Combine(workspace, "GitHub.copilot-chat", "transcripts", "s1.jsonl");

        await Assert.That(CopilotPaths.ChatSessionLogFor(transcript)).IsEqualTo(Path.Combine(workspace, "chatSessions", "s1.jsonl"));
        await Assert.That(CopilotPaths.ChatSessionLogFor(Path.Combine("/h", ".copilot", "session-state", "s1", "events.jsonl"))).IsNull();
    }

    [Test]
    public async Task VS_Code_workspace_storage_is_looked_for_in_stable_and_insiders() {
        var dirs = Under("/h").VsCodeWorkspaceStorageDirs;

        if (OperatingSystem.IsWindows()) {
            await Assert.That(dirs.All(d => d.EndsWith(Path.Combine("User", "workspaceStorage"), StringComparison.Ordinal))).IsTrue();
            return;
        }

        var userData = OperatingSystem.IsMacOS() ? Path.Combine("/h", "Library", "Application Support") : Path.Combine("/h", ".config");
        await Assert.That(dirs).IsEquivalentTo([
            Path.Combine(userData, "Code", "User", "workspaceStorage"),
            Path.Combine(userData, "Code - Insiders", "User", "workspaceStorage"),
        ]);
    }
}
