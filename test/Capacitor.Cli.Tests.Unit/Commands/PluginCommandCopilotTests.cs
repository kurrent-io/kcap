using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

// `plugin install/remove --copilot` also (un)registers the kcap MCP servers in
// ~/.copilot/mcp-config.json (McpConfigShape.Copilot → per-entry type:"stdio").
// These use an explicit PluginEnvironment rooted at a TempHome, and clear
// COPILOT_HOME for the test's duration so CopilotPaths resolves under the fake
// home (COPILOT_HOME otherwise replaces the entire ~/.copilot path). The
// `--if-installed` refresh branch is used (hooks pre-seeded, version marker
// dropped) so the "kcap on PATH" precheck on the fresh-install path never runs.
public class PluginCommandCopilotTests {
    [Test]
    public async Task install_copilot_registers_mcp_servers_preserving_user_entries() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        // Installed-but-stale hooks so `--if-installed` refreshes (and registers MCP).
        PluginCommand.InstallCopilotHooks(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);
        CopilotHooksInstaller.DeleteMarker(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);

        // A user-authored MCP server that must survive registration.
        await File.WriteAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson, """
            {"mcpServers":{"my-tool":{"type":"stdio","command":"my-tool","args":["serve"]}}}
            """);

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "install", "--copilot", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson))!.AsObject();
        var servers = root["mcpServers"]!.AsObject();
        // Registered command is the resolved native binary (injected seam), not the wrapper-resolved "kcap".
        await Assert.That(servers["kcap-review"]!["command"]!.GetValue<string>()).IsEqualTo(TestBinaryPath);
        await Assert.That(servers["kcap-review"]!["type"]!.GetValue<string>()).IsEqualTo("stdio");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-sessions");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-flows");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-memory");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-analytics");
        await Assert.That(servers["my-tool"]).IsNotNull(); // user server preserved
    }

    [Test]
    public async Task install_copilot_skip_flag_leaves_mcp_config_untouched() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        PluginCommand.InstallCopilotHooks(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);
        CopilotHooksInstaller.DeleteMarker(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(
            ["plugin", "install", "--copilot", "--if-installed", "--skip-copilot-mcp"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson)).IsFalse();
    }

    [Test]
    public async Task install_copilot_turns_on_tool_search_keeping_other_settings() {
        using var home = new TempHome();
        var env      = TestEnv(home.Path);
        var settings = env.Harnesses.Of<CopilotHarness>().Paths.SettingsJson;

        PluginCommand.InstallCopilotHooks(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);
        CopilotHooksInstaller.DeleteMarker(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);
        await File.WriteAllTextAsync(settings, """{"banner":"never"}""");

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "install", "--copilot", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settings))!.AsObject();
        await Assert.That(root["toolSearch"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["banner"]!.GetValue<string>()).IsEqualTo("never");
    }

    [Test]
    [Arguments("--skip-copilot-tool-search")]
    [Arguments("--skip-copilot-mcp")]
    public async Task install_copilot_leaves_tool_search_alone_when_skipped(string flag) {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        PluginCommand.InstallCopilotHooks(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);
        CopilotHooksInstaller.DeleteMarker(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(
            ["plugin", "install", "--copilot", "--if-installed", flag]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.Harnesses.Of<CopilotHarness>().Paths.SettingsJson)).IsFalse();
    }

    [Test]
    public async Task install_copilot_if_installed_does_not_write_mcp_config_when_never_opted_in() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        // No hooks seeded → --if-installed no-ops before touching hooks OR mcp-config.
        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "install", "--copilot", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson)).IsFalse();
    }

    [Test]
    public async Task install_copilot_if_installed_heals_mcp_and_instructions_when_hooks_current() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        // Hooks installed AND marker already current → the refresh must NOT rewrite hooks, but must
        // still (re)create the separate MCP + instructions files if they're missing (self-heal).
        PluginCommand.InstallCopilotHooks(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson); // writes hooks + current marker

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "install", "--copilot", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson)).IsTrue();
        await Assert.That(File.Exists(env.Harnesses.Of<CopilotHarness>().Paths.InstructionsMd)).IsTrue();
        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson))!.AsObject()["mcpServers"]!.AsObject();
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-review");
    }

    [Test]
    public async Task install_copilot_if_installed_heals_mcp_and_instructions_when_hook_rewrite_fails() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        // Installed (stale marker → IsInstalled true, hooksCurrent false) but the hooks rewrite will
        // FAIL: make the kcap.json path a directory so InstallCopilotHooks's write throws → returns
        // false. The refresh must warn on the hook failure but still heal the independent MCP +
        // instructions files rather than bailing.
        var hooksDir = System.IO.Path.GetDirectoryName(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson)!;
        Directory.CreateDirectory(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);   // kcap.json is a directory → write fails
        await File.WriteAllTextAsync(System.IO.Path.Combine(hooksDir, CopilotHooksInstaller.MarkerFileName), "0.0.0-stale");

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "install", "--copilot", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);                  // refresh swallows the hook-write failure

        await Assert.That(File.Exists(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson)).IsTrue();   // MCP healed despite the hook failure
        await Assert.That(File.Exists(env.Harnesses.Of<CopilotHarness>().Paths.InstructionsMd)).IsTrue();  // instructions healed too
    }

    [Test]
    public async Task remove_copilot_unregisters_mcp_servers_preserving_user_entries() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        // Seed mcp-config.json as a prior install would (ownership marker present),
        // then splice in a user-authored server that must survive removal.
        JsonMcpConfigWriter.Register(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson, KcapMcpServers.All, McpConfigShape.Copilot, cwd: null, new McpMarker("copilot", env.Home));
        var seeded = JsonNode.Parse(await File.ReadAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson))!.AsObject();
        seeded["mcpServers"]!["my-tool"] = JsonNode.Parse("""{"type":"stdio","command":"my-tool","args":["serve"]}""");
        await File.WriteAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson, seeded.ToJsonString());

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "remove", "--copilot"]);
        await Assert.That(exit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson))!.AsObject();
        var servers = root["mcpServers"]!.AsObject();
        var keys    = servers.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(keys).DoesNotContain("kcap-sessions");
        await Assert.That(keys).DoesNotContain("kcap-flows");
        await Assert.That(keys).DoesNotContain("kcap-memory");
        await Assert.That(servers["my-tool"]).IsNotNull(); // user server preserved
    }

    [Test]
    public async Task remove_copilot_retains_marker_on_failed_unregister_then_retry_removes_entries() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        JsonMcpConfigWriter.Register(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson, KcapMcpServers.All, McpConfigShape.Copilot, cwd: null, new McpMarker("copilot", env.Home));
        var installed = await File.ReadAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson); // valid content to restore after the "fix"
        await Assert.That(new McpMarker("copilot", env.Home).Owned(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson).ToArray()).IsNotEmpty();

        // The config is temporarily malformed/unreadable → Unregister fails-closed.
        await File.WriteAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson, "{ not valid json");

        var failExit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "remove", "--copilot"]);
        await Assert.That(failExit).IsEqualTo(1);                                                  // failed MCP unregister propagates
        await Assert.That(new McpMarker("copilot", env.Home).Owned(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson).ToArray()).IsNotEmpty();  // marker RETAINED for retry

        // User fixes the file (kcap entries intact); the retry now succeeds and cleans up.
        await File.WriteAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson, installed);
        var retryExit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "remove", "--copilot"]);
        await Assert.That(retryExit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson))!.AsObject();
        var servers = root["mcpServers"] as JsonObject;
        var keys    = servers?.Select(kv => kv.Key).ToArray() ?? [];
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(new McpMarker("copilot", env.Home).Owned(env.Harnesses.Of<CopilotHarness>().Paths.McpConfigJson).ToArray()).IsEmpty();  // marker cleared after clean removal
    }

    [Test]
    public async Task install_copilot_installs_instructions_preserving_user_content() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        PluginCommand.InstallCopilotHooks(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);
        CopilotHooksInstaller.DeleteMarker(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);

        // A pre-existing user instructions file that must survive.
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(env.Harnesses.Of<CopilotHarness>().Paths.InstructionsMd)!);
        await File.WriteAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.InstructionsMd, "# My rules\n\nAlways use tabs.\n");

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "install", "--copilot", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        var content = await File.ReadAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.InstructionsMd);
        await Assert.That(content).Contains("Always use tabs.");                       // user content preserved
        await Assert.That(content).Contains(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content).Contains("Prefer kcap tools");
    }

    [Test]
    public async Task install_copilot_skip_instructions_flag_leaves_file_untouched() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        PluginCommand.InstallCopilotHooks(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);
        CopilotHooksInstaller.DeleteMarker(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson);

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(
            ["plugin", "install", "--copilot", "--if-installed", "--skip-copilot-instructions"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.Harnesses.Of<CopilotHarness>().Paths.InstructionsMd)).IsFalse();
    }

    [Test]
    public async Task remove_copilot_strips_instructions_block_keeping_user_content() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(env.Harnesses.Of<CopilotHarness>().Paths.InstructionsMd)!);
        await File.WriteAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.InstructionsMd, "# My rules\n\nAlways use tabs.\n");
        AgentInstructionsWriter.Write(env.Harnesses.Of<CopilotHarness>().Paths.InstructionsMd, KcapAgentInstructions.Body);

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "remove", "--copilot"]);
        await Assert.That(exit).IsEqualTo(0);

        var content = await File.ReadAllTextAsync(env.Harnesses.Of<CopilotHarness>().Paths.InstructionsMd);
        await Assert.That(content).Contains("Always use tabs.");
        await Assert.That(content).DoesNotContain(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content).DoesNotContain("Prefer kcap tools");
    }

    // Deterministic native-binary path: registration writes the resolved binary as the command
    // (default: the running process), so tests inject their own value and assert that,
    // never blessing whatever executable happens to run the suite.
    [Test]
    public async Task A_refresh_repairs_a_hook_file_missing_entries() {
        using var home = new TempHome();
        var env   = TestEnv(home.Path);
        var hooks = env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(hooks)!);
        await File.WriteAllTextAsync(hooks, """{"version":1,"hooks":{"sessionStart":[]}}""");
        await File.WriteAllTextAsync(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(hooks)!, CopilotHooksInstaller.MarkerFileName), CapacitorVersion.Current());

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "install", "--copilot", "--if-installed"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(hooks))!["hooks"]!["sessionStart"]![0]!["command"]!.GetValue<string>())
            .Contains($"'{TestBinaryPath}' hook --copilot --event sessionStart");
    }

    /// <summary>A hook can name another binary at any version: an update, or an install moved between roots at the same one.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_refresh_rewrites_a_stale_hook_command_to_the_running_binary(bool sameVersion) {
        using var home = new TempHome();
        var env   = TestEnv(home.Path);
        var hooks = env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(hooks)!);
        await File.WriteAllTextAsync(hooks, """{"version":1,"hooks":{"sessionStart":[{"type":"command","command":"'/old/kcap' hook --copilot --event sessionStart"}]}}""");
        await File.WriteAllTextAsync(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(hooks)!, CopilotHooksInstaller.MarkerFileName), sameVersion ? CapacitorVersion.Current() : "0.0.1");

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "install", "--copilot", "--if-installed"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(hooks)).DoesNotContain("/old/kcap");
        await Assert.That(JsonNode.Parse(await File.ReadAllTextAsync(hooks))!["hooks"]!["sessionStart"]![0]!["command"]!.GetValue<string>())
            .Contains($"'{TestBinaryPath}' hook --copilot --event sessionStart");
    }

    /// <summary>The hooks name the binary, so a kcap that is not on PATH yet can still install them.</summary>
    [Test]
    public async Task A_first_install_does_not_need_kcap_on_PATH() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        var exit = await new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "install", "--copilot"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(env.Harnesses.Of<CopilotHarness>().Paths.KcapHooksJson)).IsTrue();
    }

    internal const string TestBinaryPath = "/opt/kcap-test/bin/kcap";

    static PluginEnvironment TestEnv(string fakeHome) => new(
        Home:     new(fakeHome),
        Profiles:          new ProfileConfig(),
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    ) {
        Harnesses = TestHarnesses.Under(new(fakeHome)),
        Binaries  = TestBinaries.None,
        ResolveMcpBinaryPath = () => TestBinaryPath
    };

}
