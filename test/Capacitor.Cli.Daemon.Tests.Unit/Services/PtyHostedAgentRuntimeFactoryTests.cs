using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="RuntimeStartContext.CapacitorPath"/> must reach <see cref="ReviewLaunchBuilder.BuildAsync"/>
/// as the review-launch MCP command: the review agent runs <c>kcap mcp review</c>, and the vendor
/// CLI (<c>launcher.CliPath</c>) has no such subcommand.
/// </summary>
public class PtyHostedAgentRuntimeFactoryTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    sealed class RecordingLauncher(string vendor, string cliPath) : IHostedAgentLauncher {
        public string Vendor  { get; } = vendor;
        public string CliPath { get; } = cliPath;
        public bool   SupportsUnattended { get; init; } = true;

        public LauncherContext? LastPrepareCtx { get; private set; }

        public bool IsAvailable() => true;

        public void Prepare(LauncherContext ctx) => LastPrepareCtx = ctx;

        public LaunchArgs BuildArgs(LauncherContext ctx) => new(Args: [], McpConfigPath: null);

        public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) =>
            new(Args: [.. userArgs], McpConfigPath: null);

        public void Cleanup(AgentInstance agent) { }
    }

    static RuntimeStartContext BuildReviewContext(string vendor, string capacitorPath) =>
        new(
            AgentId: "agent-review-1",
            Vendor: vendor,
            SourceRepoPath: "/repo",
            Worktree: new WorktreeInfo("/repo/.worktrees/agent-review-1", "review-branch", "/repo"),
            Prompt: null,
            Model: "opus",
            Effort: null,
            Tools: null,
            IsReview: true,
            IsReviewFlow: false,
            Review: new ReviewLaunchInfo("acme", "widgets", 42),
            Cols: 120,
            Rows: 40,
            ServerUrl: "https://srv",
            DaemonBridgeUrl: null,
            CapacitorPath: capacitorPath
        );

    /// Real directories, unlike BuildInteractiveContext's: ClaudeLauncher.Prepare writes into the
    /// worktree and the home.
    RuntimeStartContext BuildClaudeLaunchContext(string? permissionMode) {
        var repo     = Tmp.CreateDir("repo");
        var worktree = Tmp.CreateDir("wt");
        return new(
            AgentId: "agent-mode-1",
            Vendor: "claude",
            SourceRepoPath: repo,
            Worktree: new WorktreeInfo(worktree, "wt-branch", repo),
            Prompt: "build it",
            Model: "default",
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: false,
            Review: null,
            Cols: 120,
            Rows: 40,
            ServerUrl: "",
            DaemonBridgeUrl: null,
            CapacitorPath: "kcap",
            PermissionMode: permissionMode
        );
    }

    /// The runtime-context→launcher handoff: the launcher's own tests build a LauncherContext by
    /// hand, so dropping `PermissionMode = ctx.PermissionMode` here would leave them green.
    [Test]
    public async Task Interactive_launch_hands_the_permission_mode_to_the_launcher() {
        var launcher = new RecordingLauncher("claude", cliPath: "/opt/vendor/claude");
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, new NullPtyProcessFactory(), NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        var ctx   = BuildInteractiveContext("claude") with { PermissionMode = "acceptEdits" };
        var start = await factory.StartAsync(ctx, CancellationToken.None);

        try {
            await Assert.That(launcher.LastPrepareCtx!.PermissionMode).IsEqualTo("acceptEdits");
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task Interactive_claude_launch_spawns_exactly_one_selected_mode_pair() {
        var config   = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var launcher = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var pty      = new SpyPtyProcessFactory();
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, pty, NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        var start = await factory.StartAsync(BuildClaudeLaunchContext("auto"), CancellationToken.None);

        try {
            var args = pty.LastArgs!;
            await Assert.That(args.Count(a => a == "--permission-mode")).IsEqualTo(1);
            await Assert.That(args[Array.IndexOf(args, "--permission-mode") + 1]).IsEqualTo("auto");
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    /// The orchestrator only sees the factory, so a launcher that answers but is never asked would pass its own tests.
    [Test]
    public async Task Claude_reports_lost_work_from_its_transcript_and_another_vendor_reports_none() {
        var config  = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var claude  = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var factory = new PtyHostedAgentRuntimeFactory(claude, new NullPtyProcessFactory(), NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);
        var other   = new PtyHostedAgentRuntimeFactory(new RecordingLauncher("gemini", "/opt/vendor/gemini"), new NullPtyProcessFactory(), NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);
        var path    = Tmp.CreateFile("session.jsonl", """
            {"type":"user","timestamp":"2026-11-04T17:28:30.000Z","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<status>stopped</status>\n</task-notification>"}}

            """);

        await Assert.That(factory.ReportsLostWork(path)).IsTrue();
        await Assert.That(factory.ReportsLostWork(null)).IsFalse();
        await Assert.That(other.ReportsLostWork(path)).IsFalse();
    }

    [Test]
    public async Task Claude_resumes_an_owned_reviewer_but_not_a_borrowed_one() {
        var config  = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var claude  = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var factory = new PtyHostedAgentRuntimeFactory(claude, new NullPtyProcessFactory(), NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);
        var other   = new PtyHostedAgentRuntimeFactory(new RecordingLauncher("gemini", "/opt/vendor/gemini"), new NullPtyProcessFactory(), NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        await Assert.That(factory.SupportsReviewerRestartResume(null, Core.LocalIpc.WorkLocation.OwnedWorktree)).IsTrue();
        await Assert.That(factory.SupportsReviewerRestartResume(null, Core.LocalIpc.WorkLocation.BorrowedCwd)).IsFalse();
        await Assert.That(other.SupportsReviewerRestartResume(null, Core.LocalIpc.WorkLocation.OwnedWorktree)).IsFalse();
    }

    /// <summary>The hook runs as a child of the agent, so the PTY's environment is where it reads the
    /// session kind. The flow agent id stays exclusive to the flow-result server's own config.</summary>
    [Test]
    public async Task A_flow_participant_is_marked_with_its_kind_and_role_and_never_the_flow_agent_id() {
        var config   = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var launcher = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var pty      = new SpyPtyProcessFactory();
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, pty, NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        var start = await factory.StartAsync(
            BuildClaudeLaunchContext(null) with { IsReviewFlow = true, FlowRole = "reviewer" }, CancellationToken.None);

        try {
            await Assert.That(pty.LastEnv!["KCAP_SESSION_KIND"]).IsEqualTo("flow_participant");
            await Assert.That(pty.LastEnv["KCAP_FLOW_ROLE"]).IsEqualTo("reviewer");
            await Assert.That(pty.LastEnv.ContainsKey("KCAP_FLOW_AGENT_ID")).IsFalse();
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task An_interactive_launch_is_a_hosted_agent_with_no_role() {
        var config   = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var launcher = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var pty      = new SpyPtyProcessFactory();
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, pty, NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        var start = await factory.StartAsync(BuildClaudeLaunchContext(null), CancellationToken.None);

        try {
            await Assert.That(pty.LastEnv!["KCAP_SESSION_KIND"]).IsEqualTo("hosted_agent");
            await Assert.That(pty.LastEnv.ContainsKey("KCAP_FLOW_ROLE")).IsFalse();
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>A private launch carries nothing that would link its session to the agent on the
    /// server, as a <c>--private</c> local spawn does not.</summary>
    [Test]
    public async Task A_private_launch_carries_no_hosted_agent_identity() {
        var config   = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var launcher = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var pty      = new SpyPtyProcessFactory();
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, pty, NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        var start = await factory.StartAsync(
            BuildClaudeLaunchContext(null) with { IsPrivate = true, DaemonBridgeUrl = "http://127.0.0.1:9" }, CancellationToken.None);

        try {
            await Assert.That(pty.LastEnv!.ContainsKey(HostedAgent.AgentIdVar)).IsFalse();
            await Assert.That(pty.LastEnv.ContainsKey(HostedAgent.RenderedVar)).IsFalse();
            await Assert.That(pty.LastEnv.ContainsKey(HostedAgent.BridgeUrlVar)).IsFalse();
            await Assert.That(pty.LastEnv.ContainsKey("KCAP_SESSION_KIND")).IsFalse();
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>The PTY spawn applies the overlay over the daemon's environment with these removed,
    /// so a daemon started inside a participant cannot pass its role on.</summary>
    [Test]
    public async Task The_pty_scrub_covers_the_session_markers() {
        await Assert.That(PtyEnvScrub.HostedAgentVars).Contains("KCAP_SESSION_KIND");
        await Assert.That(PtyEnvScrub.HostedAgentVars).Contains("KCAP_FLOW_ROLE");
    }

    static readonly string LongPrompt = new('d', CommandLinePrompt.ArgumentBudget + 1);

    /// A kcap plugin enabled and not yet installed, as on a machine that has never started Claude.
    void WireKcapPlugin() {
        var settings = TestHarnesses.Under(Home).Of<ClaudeHarness>().Paths.UserSettings;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, """{ "enabledPlugins": { "kcap@kcap": true } }""");
    }

    (PtyHostedAgentRuntimeFactory Factory, SpyPtyProcessFactory Pty, RecordingScriptedPtyProcess Process) NewPasteFactory() {
        var config   = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var launcher = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var process  = new RecordingScriptedPtyProcess();
        var pty      = new SpyPtyProcessFactory(process);
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, pty, NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);
        return (factory, pty, process);
    }

    static bool PastesALaunchPrompt(HostedRuntimeStart start) => ((PtyHostedAgentRuntime) start.Runtime).LaunchPromptDelivery is not null;

    /// <summary>Drains the runtime's output the way the orchestrator's read loop does.</summary>
    static Task PumpAsync(IHostedAgentRuntime runtime) =>
        Task.Run(async () => { await foreach (var _ in runtime.ReadOutputAsync()) { } });

    static async Task WaitForWritesAsync(RecordingScriptedPtyProcess process, int count) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (process.Writes.Count < count && DateTime.UtcNow < deadline) await Task.Delay(25);
    }

    /// <summary>Any local user can read a command line, and <c>pkill -f</c> matches it, so the prompt
    /// never appears there, even when it is short. It reaches the agent as its first message instead,
    /// pasted once the TUI is ready for it.</summary>
    [Test]
    public async Task A_prompt_is_kept_off_the_command_line_and_pasted_once_the_tui_is_ready() {
        var (factory, pty, process) = NewPasteFactory();
        var start = await factory.StartAsync(
            BuildClaudeLaunchContext(null) with { Prompt = "build it\nthen test it" }, CancellationToken.None);

        try {
            await Assert.That(pty.LastArgs!.Any(a => a.Contains("build it", StringComparison.Ordinal))).IsFalse();

            _ = PumpAsync(start.Runtime);
            process.Emit("\x1b[?2004hwelcome banner");
            await WaitForWritesAsync(process, 2);

            await Assert.That(process.Writes[0]).IsEqualTo("\x1b[200~build it\nthen test it\x1b[201~");
            await Assert.That(process.Writes[1]).IsEqualTo("\r");
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>A resume that pastes nothing still holds the agent's first message until its terminal
    /// is ready, as a launch prompt would be held.</summary>
    [Test]
    public async Task A_resume_without_a_prompt_holds_its_first_message_until_the_tui_is_ready() {
        var (factory, _, process) = NewPasteFactory();
        var start = await factory.StartAsync(
            BuildClaudeLaunchContext(null) with { Prompt = "", ResumeSessionId = "0e61520e-4920-47e7-8b93-8ece488d010b" }, CancellationToken.None);

        try {
            await Assert.That(PastesALaunchPrompt(start)).IsFalse();

            _ = PumpAsync(start.Runtime);
            var message = start.Runtime.SendUserInputAsync("carry on");
            await Task.Delay(300);
            await Assert.That(process.Writes).IsEmpty();

            process.Emit("\x1b[?2004hwelcome back");
            await message.WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(string.Join("|", process.Writes)).IsEqualTo("\x1b[200~carry on\x1b[201~|\r");
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task A_slash_command_stays_on_the_command_line() {
        var (factory, pty, process) = NewPasteFactory();
        var start = await factory.StartAsync(BuildClaudeLaunchContext(null) with { Prompt = "/review 42" }, CancellationToken.None);

        try {
            await Assert.That(pty.LastArgs![^1]).IsEqualTo("/review 42");
            await Assert.That(PastesALaunchPrompt(start)).IsFalse();
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>An agent that quits before its prompt arrives leaves no other trace that the task
    /// never reached it.</summary>
    [Test]
    public async Task An_agent_that_exits_before_its_launch_prompt_is_logged() {
        var config   = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var launcher = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var process  = new RecordingScriptedPtyProcess();
        var logger   = new CapturingLogger<PtyHostedAgentRuntimeFactory>();
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, new SpyPtyProcessFactory(process), logger, TimeProvider.System, Home);

        var start = await factory.StartAsync(BuildClaudeLaunchContext(null), CancellationToken.None);

        try {
            process.Exit();
            await ((PtyHostedAgentRuntime) start.Runtime).LaunchPromptDelivery!.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => logger.Entries.Any(e => e.Level == LogLevel.Warning));

            await Assert.That(logger.Entries.Single(e => e.Level == LogLevel.Warning).Message).Contains("agent-mode-1");
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>An agent whose hooks reach the daemon counts its prompt delivered only once a turn is
    /// relayed, and the daemon log shows both the paste and the confirmation.</summary>
    [Test]
    public async Task A_launch_whose_hooks_reach_the_daemon_logs_its_prompt_confirmed() {
        WireKcapPlugin();
        var config   = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var launcher = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var process  = new RecordingScriptedPtyProcess();
        var logger   = new CapturingLogger<PtyHostedAgentRuntimeFactory>();
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, new SpyPtyProcessFactory(process), logger, TimeProvider.System, Home);

        var start = await factory.StartAsync(
            BuildClaudeLaunchContext(null) with { DaemonBridgeUrl = "http://127.0.0.1:9" }, CancellationToken.None);

        try {
            _ = PumpAsync(start.Runtime);
            process.Emit("\x1b[?2004hwelcome banner");
            start.Runtime.NoteSessionStarted();
            await WaitForWritesAsync(process, 2);
            start.Runtime.NoteTurnRelayed();

            var outcome = await start.Runtime.LaunchPromptDelivery!.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => InformationLines(logger).Length >= 2);

            await Assert.That(outcome).IsEqualTo(LaunchPromptOutcome.Confirmed);
            await Assert.That(InformationLines(logger)[0]).Contains("pasted and submitted");
            await Assert.That(InformationLines(logger)[1]).Contains("received its launch prompt");
            await Assert.That(InformationLines(logger).All(line => line.Contains("agent-mode-1"))).IsTrue();
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>A private agent's hooks cannot reach the daemon, so no turn could ever confirm its prompt:
    /// waiting for one would only send a stray Enter and a false warning.</summary>
    [Test]
    public async Task A_private_launch_does_not_wait_to_see_its_prompt_arrive() {
        WireKcapPlugin();
        var (factory, _, process) = NewPasteFactory();
        var start = await factory.StartAsync(
            BuildClaudeLaunchContext(null) with { IsPrivate = true, DaemonBridgeUrl = "http://127.0.0.1:9" }, CancellationToken.None);

        try {
            _ = PumpAsync(start.Runtime);
            process.Emit("\x1b[?2004hwelcome banner");

            var outcome = await start.Runtime.LaunchPromptDelivery!.WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(outcome).IsEqualTo(LaunchPromptOutcome.Delivered);
            await Assert.That(process.Writes.Count).IsEqualTo(2);
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>A machine signed in but never started: the plugin is enabled with no install record
    /// yet, and a first-run screen sits where the composer will be.</summary>
    [Test]
    public async Task A_claude_launch_with_hooks_holds_its_prompt_for_session_start() {
        WireKcapPlugin();
        var (factory, _, process) = NewPasteFactory();
        var start = await factory.StartAsync(
            BuildClaudeLaunchContext(null) with { DaemonBridgeUrl = "http://127.0.0.1:9" }, CancellationToken.None);

        try {
            _ = PumpAsync(start.Runtime);
            process.Emit("\x1b[?2004hfirst-run screen");
            await Task.Delay(PtyHostedAgentRuntime.LaunchPromptQuietWindow * 2);

            await Assert.That(process.Writes).IsEmpty();

            start.Runtime.NoteSessionStarted();
            await WaitForWritesAsync(process, 2);

            await Assert.That(process.Writes.Count).IsEqualTo(2);
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>A project-scoped install lives in the repository's settings.local.json, which the
    /// launch copies into the worktree, so the account's own settings say nothing about it.</summary>
    [Test]
    public async Task A_claude_launch_with_a_project_scoped_plugin_holds_its_prompt_for_session_start() {
        var (factory, _, process) = NewPasteFactory();
        var ctx = BuildClaudeLaunchContext(null) with { DaemonBridgeUrl = "http://127.0.0.1:9" };
        Tmp.CreateFile("repo/.claude/settings.local.json", """{ "enabledPlugins": { "kcap@kcap": true } }""");

        var start = await factory.StartAsync(ctx, CancellationToken.None);

        try {
            _ = PumpAsync(start.Runtime);
            process.Emit("\x1b[?2004hfirst-run screen");
            await Task.Delay(PtyHostedAgentRuntime.LaunchPromptQuietWindow * 2);

            await Assert.That(process.Writes).IsEmpty();
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>With no plugin no hook ever fires, whatever the bridge says: a hold would strand the
    /// prompt and a retry Enter would land on whatever is on screen.</summary>
    [Test]
    public async Task A_claude_launch_without_the_plugin_stays_on_the_output_checks_and_sends_no_retry() {
        var (factory, _, process) = NewPasteFactory();
        var start = await factory.StartAsync(
            BuildClaudeLaunchContext(null) with { DaemonBridgeUrl = "http://127.0.0.1:9" }, CancellationToken.None);

        try {
            _ = PumpAsync(start.Runtime);
            process.Emit("\x1b[?2004hwelcome banner");

            var outcome = await start.Runtime.LaunchPromptDelivery!.WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(outcome).IsEqualTo(LaunchPromptOutcome.Delivered);
            await Assert.That(process.Writes.Count).IsEqualTo(2);
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>Nothing shows Codex fires SessionStart ahead of the first input. If it fires on the
    /// first turn, a hold would wait for a hook that waits for the paste, so this stays off until
    /// someone proves otherwise.</summary>
    [Test]
    public async Task Only_claude_holds_its_prompt_for_session_start() {
        var config = new DaemonConfig { ClaudePath = "claude", CodexPath = "codex", ServerUrl = "", CapacitorPath = "kcap" };
        IHostedAgentLauncher claude = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        IHostedAgentLauncher codex  = new CodexLauncher(config, TestHarnesses.Under(Home), NullLogger<CodexLauncher>.Instance);

        await Assert.That(claude.SessionStartMeansComposerUp).IsTrue();
        await Assert.That(codex.SessionStartMeansComposerUp).IsFalse();
    }

    /// <summary>A hold has no time limit, so the daemon log has to say why an agent sits idle.</summary>
    [Test]
    public async Task A_launch_prompt_held_too_long_is_logged() {
        WireKcapPlugin();
        var time     = new FakeTimeProvider();
        var config   = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var launcher = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var process  = new RecordingScriptedPtyProcess();
        var logger   = new CapturingLogger<PtyHostedAgentRuntimeFactory>();
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, new SpyPtyProcessFactory(process), logger, time, Home);

        var start = await factory.StartAsync(
            BuildClaudeLaunchContext(null) with { DaemonBridgeUrl = "http://127.0.0.1:9" }, CancellationToken.None);

        try {
            for (var i = 0; i < 100 && !start.Runtime.LaunchPromptUnconfirmed; i++) {
                time.Advance(TimeSpan.FromMilliseconds(250));
                await Task.Delay(10);
            }

            await WaitUntilAsync(() => WarningLines(logger).Length >= 1);

            await Assert.That(WarningLines(logger).Single()).Contains("SessionStart");
            await Assert.That(process.Writes).IsEmpty();
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    static string[] WarningLines(CapturingLogger<PtyHostedAgentRuntimeFactory> logger) {
        lock (logger.Entries) return [.. logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)];
    }

    static string[] InformationLines(CapturingLogger<PtyHostedAgentRuntimeFactory> logger) {
        lock (logger.Entries) return [.. logger.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message)];
    }

    static async Task WaitUntilAsync(Func<bool> done) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(25);
    }

    /// <summary>A leading absolute path is not a slash command, so it is pasted like any prose.</summary>
    [Test]
    public async Task A_prompt_that_starts_with_an_absolute_path_is_pasted() {
        var (factory, pty, process) = NewPasteFactory();
        var start = await factory.StartAsync(
            BuildClaudeLaunchContext(null) with { Prompt = "/Users/me/notes.md has the plan" }, CancellationToken.None);

        try {
            await Assert.That(pty.LastArgs!.Any(a => a.Contains("notes.md", StringComparison.Ordinal))).IsFalse();
            await Assert.That(PastesALaunchPrompt(start)).IsTrue();
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>An overlong command line fails a Windows spawn outright, so there even a slash command
    /// is pasted; elsewhere it fits and keeps working as a command.</summary>
    [Test]
    public async Task An_overlong_slash_command_is_pasted_only_on_Windows() {
        var (factory, pty, process) = NewPasteFactory();
        var prompt = "/" + LongPrompt;
        var start  = await factory.StartAsync(BuildClaudeLaunchContext(null) with { Prompt = prompt }, CancellationToken.None);

        try {
            if (OperatingSystem.IsWindows()) {
                await Assert.That(pty.LastArgs!.Contains(prompt)).IsFalse();
                await Assert.That(PastesALaunchPrompt(start)).IsTrue();
            } else {
                await Assert.That(pty.LastArgs![^1]).IsEqualTo(prompt);
                await Assert.That(PastesALaunchPrompt(start)).IsFalse();
            }
        } finally {
            process.Exit();
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task A_launch_that_does_not_put_the_prompt_on_the_command_line_pastes_nothing() {
        var launcher = new RecordingLauncher("claude", cliPath: "/opt/vendor/claude");
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, new NullPtyProcessFactory(), NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        var start = await factory.StartAsync(BuildInteractiveContext("claude") with { Prompt = "build it" }, CancellationToken.None);

        try {
            await Assert.That(PastesALaunchPrompt(start)).IsFalse();
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }


    [Test]
    public async Task Review_launch_builds_the_MCP_command_from_CapacitorPath_not_the_agent_CliPath() {
        var launcher   = new RecordingLauncher("claude", cliPath: "/opt/vendor/claude");
        var ptyFactory = new NullPtyProcessFactory();
        var factory    = new PtyHostedAgentRuntimeFactory(launcher, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        var ctx = BuildReviewContext("claude", capacitorPath: "/opt/kcap/kcap");

        var start = await factory.StartAsync(ctx, CancellationToken.None);

        try {
            var reviewLaunch = launcher.LastPrepareCtx!.ReviewLaunch;
            await Assert.That(reviewLaunch).IsNotNull();

            // The MCP server descriptor's Command is what the review agent actually spawns to
            // talk to the review MCP tools — it must be the kcap binary, never the vendor CLI.
            await Assert.That(reviewLaunch!.Mcp.Command).IsEqualTo("/opt/kcap/kcap");
            await Assert.That(reviewLaunch.Mcp.Command).IsNotEqualTo("/opt/vendor/claude");

            // Claude's written MCP config file (the actual artifact claude reads to find the
            // server) must also reference the kcap path, not the vendor CLI.
            await Assert.That(reviewLaunch.McpConfigPath).IsNotNull();
            var json = await File.ReadAllTextAsync(reviewLaunch.McpConfigPath!);
            await Assert.That(json).Contains("/opt/kcap/kcap");
            await Assert.That(json).DoesNotContain("/opt/vendor/claude");
        } finally {
            if (launcher.LastPrepareCtx?.ReviewLaunch?.McpConfigPath is { } path && File.Exists(path))
                File.Delete(path);
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task Review_launch_for_codex_builds_MCP_command_from_CapacitorPath_not_the_agent_CliPath() {
        var launcher   = new RecordingLauncher("codex", cliPath: "/opt/vendor/codex");
        var ptyFactory = new NullPtyProcessFactory();
        var factory    = new PtyHostedAgentRuntimeFactory(launcher, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        var ctx = BuildReviewContext("codex", capacitorPath: "/opt/kcap/kcap");

        var start = await factory.StartAsync(ctx, CancellationToken.None);

        try {
            var reviewLaunch = launcher.LastPrepareCtx!.ReviewLaunch;
            await Assert.That(reviewLaunch).IsNotNull();
            await Assert.That(reviewLaunch!.Mcp.Command).IsEqualTo("/opt/kcap/kcap");
            await Assert.That(reviewLaunch.Mcp.Command).IsNotEqualTo("/opt/vendor/codex");
            // Codex injects the MCP server via -c overrides — no config file is written.
            await Assert.That(reviewLaunch.McpConfigPath).IsNull();
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>The factory half of the posture handoff. The orchestrator-level test proves
    /// <c>LaunchAgentCommand → RuntimeStartContext</c>, but it substitutes a spy for this factory, so
    /// dropping <c>CodexPosture = ctx.CodexPosture</c> here would leave that test green while the real
    /// launcher silently received null — launching on the default posture even though registration
    /// advertised the selected pair. <see cref="RecordingLauncher.LastPrepareCtx"/> is the same
    /// LauncherContext instance BuildArgs consumes, so asserting on it pins what the launcher sees.</summary>
    [Test]
    public async Task Codex_launch_threads_the_posture_into_the_launcher_context() {
        var launcher   = new RecordingLauncher("codex", cliPath: "/opt/vendor/codex");
        var ptyFactory = new NullPtyProcessFactory();
        var factory    = new PtyHostedAgentRuntimeFactory(launcher, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        var ctx = BuildInteractiveContext("codex") with { CodexPosture = new("danger-full-access", "untrusted") };

        var start = await factory.StartAsync(ctx, CancellationToken.None);

        try {
            await Assert.That(launcher.LastPrepareCtx).IsNotNull();
            await Assert.That(launcher.LastPrepareCtx!.CodexPosture).IsNotNull();
            await Assert.That(launcher.LastPrepareCtx!.CodexPosture!.Sandbox).IsEqualTo("danger-full-access");
            await Assert.That(launcher.LastPrepareCtx!.CodexPosture!.Approval).IsEqualTo("untrusted");
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task Codex_launch_without_a_posture_threads_null_into_the_launcher_context() {
        var launcher   = new RecordingLauncher("codex", cliPath: "/opt/vendor/codex");
        var ptyFactory = new NullPtyProcessFactory();
        var factory    = new PtyHostedAgentRuntimeFactory(launcher, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance, TimeProvider.System, Home);

        var start = await factory.StartAsync(BuildInteractiveContext("codex"), CancellationToken.None);

        try {
            await Assert.That(launcher.LastPrepareCtx).IsNotNull();
            await Assert.That(launcher.LastPrepareCtx!.CodexPosture).IsNull();
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    static RuntimeStartContext BuildInteractiveContext(string vendor) =>
        new(
            AgentId: "agent-interactive-1",
            Vendor: vendor,
            SourceRepoPath: "/repo",
            Worktree: new WorktreeInfo("/repo/.worktrees/agent-interactive-1", "wt-branch", "/repo"),
            Prompt: null,
            Model: "gpt-5.3-codex",
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: false,
            Review: null,
            Cols: 120,
            Rows: 40,
            ServerUrl: "https://srv",
            DaemonBridgeUrl: null,
            CapacitorPath: "/opt/kcap/kcap"
        );

    sealed class NullPtyProcessFactory : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string                      command,
                string[]                    args,
                string                      cwd,
                Dictionary<string, string>? extraEnv = null,
                ushort                      cols     = 120,
                ushort                      rows     = 40,
                IReadOnlyCollection<string>? removeEnv = null
            ) => new NoopPty();

        sealed class NoopPty : IPtyProcess {
            public int  Pid       => 0;
            public bool HasExited => true;
            public int? ExitCode  => 0;

            public ValueTask DisposeAsync() => default;
            public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
            public Task TerminateAsync(TimeSpan?   timeout = null) => Task.CompletedTask;

#pragma warning disable CS1998
            public async IAsyncEnumerable<byte[]> ReadOutputAsync(
                    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
                yield break;
            }
#pragma warning restore CS1998

            public Task WriteAsync(string input) => Task.CompletedTask;
            public Task WriteAsync(byte[] data) => Task.CompletedTask;
            public void Resize(ushort     cols, ushort rows) { }
            public void SendInterrupt() { }
        }
    }
}
