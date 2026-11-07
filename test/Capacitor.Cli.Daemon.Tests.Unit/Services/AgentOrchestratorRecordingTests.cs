using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Where the daemon's recorder meets a launch: the claim has to beat the process's first hook, a launch that
/// never ran gives it back, an agent's end follows its tail, and with the flag off a launch is exactly what it was.</summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorRecordingTests {
    const string AgentId = "agent-1";
    const string Resumed = "01234567-89ab-cdef-0123-456789abcdef";

    [TempDir] public required TempDir Tmp { get; init; }

    sealed class StartingFactory(Action<RuntimeStartContext>? starting = null) : IHostedAgentRuntimeFactory {
        public string CliPath            => "unused";
        public string Vendor             => "claude";
        public bool   SupportsUnattended => false;
        public bool   Fails              { get; init; }

        public RuntimeStartContext?    Context { get; private set; }
        public FakeHostedAgentRuntime? Runtime { get; private set; }

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            Context = ctx;
            starting?.Invoke(ctx);
            if (Fails) throw new InvalidOperationException("The agent would not start.");

            Runtime = new FakeHostedAgentRuntime(Vendor, emitsTerminalOutput: true) { ExitCode = 0 };

            return Task.FromResult(new HostedRuntimeStart(Runtime, McpConfigPath: null, Transcript: null));
        }
    }

    sealed class Fixture : IAsyncDisposable {
        public Fixture(
                TempDir tmp, StartingFactory factory, bool on = true, bool? serverRecords = true,
                Action<ConfigRoot>? profile = null, IPtyProcessFactory? pty = null, string? localSession = null) {
            Checkout  = tmp.CreateDir("checkout");
            Factory   = factory;
            Server    = new CaptureServerConnection();
            Transport = new FakeRecorderTransport { IsConnected = true };
            Orch = AgentOrchestratorHarness.BuildOrchestrator(
                Server, pty ?? new SpyPtyProcessFactory(),
                new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude") { PassthroughSessionId = localSession } },
                allowedRepoPath: Checkout, extraRuntimeFactories: [factory],
                configure: c => {
                    c.RecordHostedClaude = on;
                    profile?.Invoke(c.ConfigRoot);
                },
                recorder: config => {
                    Config = config;
                    var probe = new RecordingProbe();
                    if (serverRecords is { } answer) probe.Report(answer);

                    return new DaemonRecorder(
                        config, new CapacitorServer(config.ServerUrl, config.ConfigRoot, Resolutions.None(config.ConfigRoot)), probe,
                        () => {
                            Connections++;

                            return Transport;
                        },
                        (_, _, _, _) => new FakeRecorderHousekeeping(), TimeProvider.System, NullLogger<DaemonRecorder>.Instance);
                });
        }

        public string Checkout { get; }
        public StartingFactory Factory { get; }
        public CaptureServerConnection Server { get; }
        public FakeRecorderTransport Transport { get; }
        public AgentOrchestrator Orch { get; }
        public DaemonConfig Config { get; private set; } = null!;
        public int Connections { get; private set; }

        public RecorderClaims Claims => new(WatcherPaths.FromEnvironment(Config.ConfigRoot).Directory, TimeProvider.System);

        public string ClaimsDirectory => WatcherPaths.FromEnvironment(Config.ConfigRoot).Directory;

        public string Projects => new ClaudePaths(Config.Home, null).Projects;

        /// <summary>Where Claude keeps a session of the launched agent, or that session's subagent.</summary>
        public string TranscriptOf(string session, string? subagent) {
            var parent = Path.Combine(new ClaudePaths(Config.Home, null).ProjectDir(Factory.Context?.Worktree.Path ?? Checkout), $"{session}.jsonl");

            return subagent is null ? parent : ClaudeSubagentFiles.TranscriptPath(parent, subagent);
        }

        /// <summary>A directory under the projects that is a link to <paramref name="target"/>.</summary>
        public string Link(string name, string target) {
            Directory.CreateDirectory(Projects);

            return Directory.CreateSymbolicLink(Path.Combine(Projects, name), target).FullName;
        }

        public string[] ClaimFiles => Directory.Exists(ClaimsDirectory) ? Directory.GetFiles(ClaimsDirectory, "*.claim") : [];

        public LaunchAgentCommand Launch(string? resumeSession = null) =>
            new(AgentId: AgentId, Prompt: "do work", Model: "", Effort: null, RepoPath: Checkout, Tools: null, AttachmentIds: null, Vendor: "claude") {
                ResumeSessionId = resumeSession,
            };

        public async ValueTask DisposeAsync() => await Orch.DisposeAsync();
    }

    /// <summary>What the capture gate reads: the launch itself is a person's, in a checkout the list doesn't name.</summary>
    static void ScopedProfile(ConfigRoot root) =>
        File.WriteAllText(root.Path("config.json"), """
            {
              "version": 2,
              "active_profile": "default",
              "profiles": { "default": { "excluded_paths": ["/nowhere/at/all"] } },
              "profile_bindings": {}
            }
            """);

    static void UnreadableProfile(ConfigRoot root) => File.WriteAllText(root.Path("config.json"), "{ not json");

    /// <summary>The daemon takes a session ahead of its hooks only where no hook could refuse it: with an exclusion
    /// configured, this claim would upload a transcript the hook's gate keeps a watcher from ever seeing.</summary>
    [Test]
    public async Task A_launch_under_a_profile_that_scopes_capture_takes_no_claim() {
        await using var f = new Fixture(Tmp, new StartingFactory(), profile: ScopedProfile);

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.ClaimFiles).IsEmpty();
        await Assert.That(f.Connections).IsEqualTo(0);
    }

    [Test]
    public async Task A_launch_under_a_profile_it_cannot_read_takes_no_claim() {
        await using var f = new Fixture(Tmp, new StartingFactory(), profile: UnreadableProfile);

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.ClaimFiles).IsEmpty();
        await Assert.That(f.Connections).IsEqualTo(0);
    }

    [Test]
    public async Task A_resume_under_a_profile_that_scopes_capture_takes_no_claim() {
        await using var f = new Fixture(Tmp, new StartingFactory(), profile: ScopedProfile);

        await f.Orch.HandleLaunchAgentForTest(f.Launch(resumeSession: Resumed));

        await Assert.That(f.ClaimFiles).IsEmpty();
        await Assert.That(f.Connections).IsEqualTo(0);
    }

    [Test]
    public async Task A_local_spawn_under_a_profile_that_scopes_capture_takes_no_claim() {
        await using var f = new Fixture(Tmp, new StartingFactory(), profile: ScopedProfile, localSession: Resumed);

        await SpawnLocallyAsync(f);

        await Assert.That(f.ClaimFiles).IsEmpty();
        await Assert.That(f.Connections).IsEqualTo(0);
    }

    /// <summary>The hook's gate has passed by the time it hands a source over, which is how a scoped profile records.</summary>
    [Test]
    public async Task A_hook_hands_over_an_in_scope_session_under_a_profile_that_scopes_capture() {
        await using var f = new Fixture(Tmp, new StartingFactory(), profile: ScopedProfile);
        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        var reply = await HandOverAsync(f, Handed(f));

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.Recording);
    }

    [Test]
    public async Task The_boot_under_a_profile_that_scopes_capture_picks_nothing_up() {
        await using var f = new Fixture(Tmp, new StartingFactory(), profile: ScopedProfile);
        var left = new RecordedSource("gone", "fedcba9876543210fedcba9876543210", null, Tmp.CreateFile("left.jsonl", ""), "claude", null, false);
        new RecordedSourceList(f.Config.Store.StateDirectory(f.Config.Name), NullLogger.Instance).TryAdd(left);

        await f.Orch.ResumeOnBootAsync(CancellationToken.None).WithinDeadlineAsync("the boot's resumes");

        await Assert.That(f.Connections).IsEqualTo(0);
        await Assert.That(new RecordedSourceList(f.Config.Store.StateDirectory(f.Config.Name), NullLogger.Instance).Load()).IsEmpty();
    }

    /// <summary>A local spawn's first hook races the attach exactly as a server launch's does.</summary>
    [Test]
    public async Task A_local_spawns_claim_exists_before_its_process() {
        var pty = new ClaimReadingPty();
        await using var f = new Fixture(Tmp, new StartingFactory(), pty: pty, localSession: Resumed);
        pty.Read = () => f.Claims.Read(SessionIds.Canonical(Resumed)!);

        await SpawnLocallyAsync(f);

        await Assert.That(pty.AtSpawn).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    [Test]
    public async Task A_private_local_spawn_takes_no_claim() {
        await using var f = new Fixture(Tmp, new StartingFactory(), localSession: Resumed);

        await SpawnLocallyAsync(f, isPrivate: true);

        await Assert.That(f.ClaimFiles).IsEmpty();
    }

    [Test]
    public async Task A_local_spawn_that_never_ran_gives_its_claim_back() {
        await using var f = new Fixture(Tmp, new StartingFactory(), pty: new ThrowingPty(), localSession: Resumed);

        await SpawnLocallyAsync(f);

        await Assert.That(f.ClaimFiles).IsEmpty();
    }

    sealed class ClaimReadingPty : IPtyProcessFactory {
        public Func<RecorderClaimState>? Read { get; set; }
        public RecorderClaimState? AtSpawn { get; private set; }

        public IPtyProcess Spawn(string command, string[] args, string cwd, Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40, IReadOnlyCollection<string>? removeEnv = null) {
            AtSpawn = Read?.Invoke();

            return new StubPtyProcess();
        }
    }

    sealed class ThrowingPty : IPtyProcessFactory {
        public IPtyProcess Spawn(string command, string[] args, string cwd, Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40, IReadOnlyCollection<string>? removeEnv = null) =>
            throw new InvalidOperationException("The agent would not start.");
    }

    static async Task SpawnLocallyAsync(Fixture f, bool isPrivate = false) {
        var input = new MemoryStream();
        await FrameCodec.WriteAsync(input, LocalFrame.Detach(), CancellationToken.None);
        input.Position = 0;
        using var client = new DuplexTestStream(input, new MemoryStream());

        await f.Orch.HandleLocalSpawnAsync(FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate, f.Checkout, [], 80, 24), client, CancellationToken.None);
    }

    /// <summary>A hosted agent's first hook can run before anything after its start has, and with no claim to read it
    /// starts a watcher beside the recorder. The key is spelled as hooks spell it, dashless, or it reads as nobody's.</summary>
    [Test]
    public async Task The_claim_exists_before_the_agent_process_starts() {
        RecorderClaimState? atStart = null;
        Fixture? f = null;
        var factory = new StartingFactory(ctx => atStart = f!.Claims.Read(SessionIds.Canonical(ctx.PinnedSessionId)!));

        await using (f = new Fixture(Tmp, factory)) {
            await f.Orch.HandleLaunchAgentForTest(f.Launch());

            await Assert.That(factory.Context!.PinnedSessionId).IsNotNull();
            await Assert.That(atStart).IsEqualTo(RecorderClaimState.DaemonFresh);
        }
    }

    [Test]
    public async Task A_launched_session_is_recorded_from_where_Claude_writes_it() {
        await using var f = new Fixture(Tmp, new StartingFactory());

        await f.Orch.HandleLaunchAgentForTest(f.Launch());
        await WaitHarness.PollUntilAsync(() => f.Transport.Connects == 1);

        var pinned = f.Factory.Context!.PinnedSessionId!;
        var source = new RecordedSourceList(f.Config.Store.StateDirectory(f.Config.Name), NullLogger.Instance).Load().Single();

        await Assert.That(source.AgentId).IsEqualTo(AgentId);
        await Assert.That(source.SessionId).IsEqualTo(SessionIds.Canonical(pinned));
        await Assert.That(Path.GetFileName(source.TranscriptPath)).IsEqualTo($"{pinned}.jsonl");
        await Assert.That(source.Cwd).IsEqualTo(f.Factory.Context!.Worktree.Path);
        await Assert.That(source.SkipTitle).IsFalse();
    }

    /// <summary>A session the agent resumes names its id at launch, and already has its title.</summary>
    [Test]
    public async Task A_resumed_session_is_claimed_under_its_own_id() {
        await using var f = new Fixture(Tmp, new StartingFactory());

        await f.Orch.HandleLaunchAgentForTest(f.Launch(resumeSession: Resumed));
        await WaitHarness.PollUntilAsync(() => f.Transport.Connects == 1);

        var source = new RecordedSourceList(f.Config.Store.StateDirectory(f.Config.Name), NullLogger.Instance).Load().Single();

        await Assert.That(source.SessionId).IsEqualTo(SessionIds.Canonical(Resumed));
        await Assert.That(source.SkipTitle).IsTrue();
    }

    /// <summary>Nothing would beat the claim, and a stale hosted claim leaves the session to a catch-up that never comes.</summary>
    [Test]
    public async Task A_launch_that_never_ran_gives_its_claim_back() {
        await using var f = new Fixture(Tmp, new StartingFactory { Fails = true });

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.ClaimFiles).IsEmpty();
        await Assert.That(f.Connections).IsEqualTo(0);
    }

    /// <summary>A transcript line landing behind the session's end reactivates it, so the daemon's end follows the
    /// final drain.</summary>
    [Test]
    public async Task An_agents_end_follows_its_final_drain() {
        await using var f = new Fixture(Tmp, new StartingFactory());
        var endsAtDrain = -1;

        await f.Orch.HandleLaunchAgentForTest(f.Launch());
        await WaitHarness.PollUntilAsync(() => f.Transport.Connects == 1);
        f.Transport.OnDrainComplete = _ => {
            endsAtDrain = f.Server.EndSessionReasons.Count;

            return Task.CompletedTask;
        };

        f.Factory.Runtime!.ExitGate.SetResult();
        await WaitHarness.PollUntilAsync(() => f.Server.EndSessionReasons.Count == 1);

        await Assert.That(endsAtDrain).IsEqualTo(0);
        await Assert.That(f.ClaimFiles).IsEmpty();
    }

    [Test]
    public async Task With_the_flag_off_nothing_changes() {
        await using var f = new Fixture(Tmp, new StartingFactory(), on: false);

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Factory.Runtime).IsNotNull();
        await Assert.That(f.ClaimFiles).IsEmpty();
        await Assert.That(f.Connections).IsEqualTo(0);
        await Assert.That(f.Orch.RecordsHostedSessions).IsFalse();
        await Assert.That(LocalControlCapabilities.Advertised(f.Orch.RecordsHostedSessions)).IsEqualTo(LocalControlCapabilities.Current);
    }

    [Test]
    public async Task No_claim_and_no_capability_on_a_server_without_recording() {
        await using var f = new Fixture(Tmp, new StartingFactory(), serverRecords: false);

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.ClaimFiles).IsEmpty();
        await Assert.That(f.Connections).IsEqualTo(0);
        await Assert.That(f.Orch.RecordsHostedSessions).IsFalse();
    }

    /// <summary>The server's answer can be a while, and no launch waits for it: this one goes on as it always has, and its
    /// hook hands the session over once the answer is in.</summary>
    [Test]
    public async Task A_launch_ahead_of_the_servers_answer_takes_no_claim_and_does_not_wait() {
        await using var f = new Fixture(Tmp, new StartingFactory(), serverRecords: null);

        await f.Orch.HandleLaunchAgentForTest(f.Launch()).WithinDeadlineAsync("the launch");

        await Assert.That(f.ClaimFiles).IsEmpty();
        await Assert.That(f.Orch.RecordsHostedSessions).IsFalse();
    }

    [Test]
    public async Task A_recording_daemon_advertises_it() {
        await using var f = new Fixture(Tmp, new StartingFactory());

        await Assert.That(f.Orch.RecordsHostedSessions).IsTrue();
        await Assert.That(LocalControlCapabilities.Advertised(f.Orch.RecordsHostedSessions)).Contains(RecordingIpc.Capability);
    }

    static async Task<LocalFrame> AnswerAsync(Func<Stream, Task> handle) {
        using var stream = new MemoryStream();
        await handle(stream);
        stream.Position = 0;

        return (await FrameCodec.ReadAsync(stream, CancellationToken.None))!;
    }

    const string Moved = "fedcba9876543210fedcba9876543210";

    /// <summary>What a hook sends for a subagent of a session in its agent's project, unless it says otherwise.</summary>
    static string Handed(Fixture f, string agentId = AgentId, string session = Moved, string? subagent = "sub-1", string? transcript = null, string? cwd = null) =>
        JsonSerializer.Serialize(
            new RecordSourceDto(agentId, session, subagent, transcript ?? f.TranscriptOf(session, subagent), "claude", cwd, false), RecordingIpcJsonContext.Default.RecordSourceDto);

    static Task<LocalFrame> HandOverAsync(Fixture f, string handed) => AnswerAsync(stream => f.Orch.HandleLocalRecordSourceAsync(handed, stream, CancellationToken.None));

    /// <summary>A subagent's hook, or a session the agent moved to: the source starts on the daemon the hook named.</summary>
    [Test]
    public async Task A_hook_hands_its_agent_a_source() {
        await using var f = new Fixture(Tmp, new StartingFactory());
        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        var reply = await HandOverAsync(f, Handed(f));

        await Assert.That(reply.Type).IsEqualTo(FrameType.RecordSourceAck);
        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.Recording);
        await Assert.That(f.Claims.Read("fedcba9876543210fedcba9876543210-sub-1")).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    public enum Forged { OutsideTheProjects, ThroughASymlink, Relative, NotATranscript, AnotherSessionsFile, AnotherSubagentsFile, UnderAnotherSession, AnotherProject }

    /// <summary>The daemon outlives its agent and reads anything its user can, and what it reads goes to the server under
    /// the session the hook names. Each of these is a file a hook could point it at. A refusal leaves the hook to start a
    /// watcher, which runs as the user's own process and carries none of this.</summary>
    [Test]
    [Arguments(Forged.OutsideTheProjects)]
    [Arguments(Forged.ThroughASymlink)]
    [Arguments(Forged.Relative)]
    [Arguments(Forged.NotATranscript)]
    [Arguments(Forged.AnotherSessionsFile)]
    [Arguments(Forged.AnotherSubagentsFile)]
    [Arguments(Forged.UnderAnotherSession)]
    [Arguments(Forged.AnotherProject)]
    public async Task A_transcript_that_is_not_the_agents_is_refused(Forged forged) {
        Skip.When(forged == Forged.ThroughASymlink && OperatingSystem.IsWindows(), "creating a symlink needs privileges Windows CI lacks");

        await using var f = new Fixture(Tmp, new StartingFactory());
        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        var outside = Tmp.CreateDir("outside");
        var handed  = forged switch {
            Forged.OutsideTheProjects   => Handed(f, subagent: null, transcript: Tmp.CreateFile($"outside/{Moved}.jsonl", "secret")),
            Forged.ThroughASymlink      => Handed(f, subagent: null, transcript: Path.Combine(f.Link("escape", outside), $"{Moved}.jsonl")),
            Forged.Relative             => Handed(f, subagent: null, transcript: $"{Moved}.jsonl"),
            Forged.NotATranscript       => Handed(f, subagent: null, transcript: Path.ChangeExtension(f.TranscriptOf(Moved, null), ".txt")),
            Forged.AnotherSessionsFile  => Handed(f, subagent: null, transcript: f.TranscriptOf(Resumed, null)),
            Forged.AnotherSubagentsFile => Handed(f, transcript: f.TranscriptOf(Moved, "sub-2")),
            Forged.UnderAnotherSession  => Handed(f, transcript: f.TranscriptOf(Resumed, "sub-1")),
            _                           => Handed(f, subagent: null, transcript: Path.Combine(f.Projects, "-another-project", $"{Moved}.jsonl")),
        };

        var reply = await HandOverAsync(f, handed);

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.NotTheAgents);
        await Assert.That(f.ClaimFiles.Any(claim => claim.Contains(Moved))).IsFalse();
    }

    /// <summary>A project directory Claude spelled its own way still holds the agent's own session.</summary>
    [Test]
    public async Task The_agents_own_session_is_taken_from_anywhere_under_the_projects() {
        await using var f = new Fixture(Tmp, new StartingFactory());
        await f.Orch.HandleLaunchAgentForTest(f.Launch());
        var session = SessionIds.Canonical(f.Factory.Context!.PinnedSessionId)!;

        var reply = await HandOverAsync(f, Handed(f, session: session, subagent: "sub-1", transcript: Path.Combine(f.Projects, "-spelled-its-own-way", session, "subagents", "agent-sub-1.jsonl")));

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.Recording);
    }

    [Test]
    public async Task A_session_of_another_agent_is_refused() {
        await using var f = new Fixture(Tmp, new StartingFactory());
        await f.Orch.HandleLaunchAgentForTest(f.Launch());
        var taken = SessionIds.Canonical(f.Factory.Context!.PinnedSessionId)!;
        await f.Orch.HandleLaunchAgentForTest(f.Launch() with { AgentId = "agent-2" });

        var reply = await HandOverAsync(f, Handed(f, agentId: "agent-2", session: taken));

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.NotTheAgents);
    }

    [Test]
    public async Task A_working_directory_outside_the_agents_checkout_is_refused() {
        await using var f = new Fixture(Tmp, new StartingFactory());
        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        var reply = await HandOverAsync(f, Handed(f, cwd: Tmp.CreateDir("somewhere-else")));

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.NotTheAgents);
    }

    [Test]
    public async Task A_working_directory_inside_the_agents_checkout_is_taken() {
        await using var f = new Fixture(Tmp, new StartingFactory());
        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        var reply = await HandOverAsync(f, Handed(f, cwd: Path.Combine(f.Factory.Context!.Worktree.Path, "src")));

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.Recording);
    }

    /// <summary>A late hook of an agent on its way out would start a source no exit will ever finish.</summary>
    [Test]
    public async Task A_hook_of_an_agent_in_cleanup_is_refused() {
        await using var f = new Fixture(Tmp, new StartingFactory());
        await f.Orch.HandleLaunchAgentForTest(f.Launch());
        f.Orch.GetAgentForTest(AgentId)!.CleanupStarted = 1;

        var reply = await HandOverAsync(f, Handed(f));

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.Refused);
    }

    [Test]
    public async Task A_hook_of_no_agent_here_is_refused() {
        await using var f = new Fixture(Tmp, new StartingFactory());

        var reply = await HandOverAsync(f, Handed(f, agentId: "nobody"));

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.Refused);
        await Assert.That(f.ClaimFiles).IsEmpty();
    }

    [Test]
    public async Task A_hook_on_a_daemon_with_the_flag_off_is_refused() {
        await using var f = new Fixture(Tmp, new StartingFactory(), on: false);
        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        var reply = await HandOverAsync(f, Handed(f));

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.Refused);
        await Assert.That(f.ClaimFiles).IsEmpty();
    }

    [Test]
    public async Task A_malformed_hand_off_is_refused() {
        await using var f = new Fixture(Tmp, new StartingFactory());

        var reply = await AnswerAsync(stream => f.Orch.HandleLocalRecordSourceAsync("not json", stream, CancellationToken.None));

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.Refused);
    }

    /// <summary>A session-end hook's ask: the final drain has run by the time the hook hears back, so its post can't
    /// overtake the tail.</summary>
    [Test]
    public async Task A_hooks_finish_comes_back_once_the_source_has_drained() {
        await using var f = new Fixture(Tmp, new StartingFactory());
        await f.Orch.HandleLaunchAgentForTest(f.Launch());
        await WaitHarness.PollUntilAsync(() => f.Transport.Connects == 1);
        var key = SessionIds.Canonical(f.Factory.Context!.PinnedSessionId)!;

        var reply = await AnswerAsync(stream => f.Orch.HandleLocalFinishSourceAsync(key, stream, CancellationToken.None));

        await Assert.That(reply.Type).IsEqualTo(FrameType.FinishSourceAck);
        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.Finished);
        await Assert.That(f.Transport.DrainCompletes).IsEqualTo(1);
        await Assert.That(f.Claims.Read(key)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task A_finish_for_a_source_not_held_says_so() {
        await using var f = new Fixture(Tmp, new StartingFactory());

        var reply = await AnswerAsync(stream => f.Orch.HandleLocalFinishSourceAsync("fedcba9876543210fedcba9876543210", stream, CancellationToken.None));

        await Assert.That(reply.Text).IsEqualTo(RecordingIpc.NotHeld);
    }

    /// <summary>The agents stop first, so the last lines are in the file, and a hard stop of the daemon keeps what a
    /// resume will need.</summary>
    [Test]
    public async Task A_shutdown_stops_the_recorder() {
        var f = new Fixture(Tmp, new StartingFactory());
        await f.Orch.HandleLaunchAgentForTest(f.Launch());
        await WaitHarness.PollUntilAsync(() => f.Transport.Connects == 1);

        await f.DisposeAsync().AsTask().WithinDeadlineAsync("the shutdown");

        await Assert.That(f.Transport.DrainCompletes).IsEqualTo(1);
    }

    /// <summary>A restart's resumes run over what the last daemon left, and a source no agent came back for ends.</summary>
    [Test]
    public async Task The_boot_picks_up_what_the_last_daemon_left() {
        await using var f = new Fixture(Tmp, new StartingFactory());
        var leftBehind = new RecordedSource("gone", "fedcba9876543210fedcba9876543210", null, Tmp.CreateFile("left.jsonl", ""), "claude", null, false);
        new RecordedSourceList(f.Config.Store.StateDirectory(f.Config.Name), NullLogger.Instance).TryAdd(leftBehind);

        await f.Orch.ResumeOnBootAsync(CancellationToken.None).WithinDeadlineAsync("the boot's resumes");

        await WaitHarness.PollUntilAsync(() => f.ClaimFiles.Length == 0 && f.Transport.Detached.Contains(leftBehind.Key));
    }
}
