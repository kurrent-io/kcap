using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A terminal session's hook handing its session to the daemon. There is no agent to tie the session to, so the
/// daemon holds what the hook resolved against its own: profile, server, config and claims directories, the Claude
/// projects the transcript sits in, and a Claude process still running. Whatever fails, the answer is a refusal, and the
/// hook starts a watcher.</summary>
public class LocalControlRecordingTests {
    const string Session = "0123456789abcdef0123456789abcdef";

    static readonly ProcessIncarnation Claude = new(4242, "test:claude");

    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Fixture : IAsyncDisposable {
        public Fixture(bool on = true, bool? serverRecords = true, AccountStore? accounts = null) {
            Orch = AgentOrchestratorHarness.BuildOrchestrator(
                new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                configure: c => {
                    c.RecordHostedClaude = on;
                    c.Accounts           = accounts;
                },
                recorder: config => {
                    Config = config;
                    if (serverRecords is { } answer) Probe.Report(answer);

                    return Recorder = new DaemonRecorder(
                        config, new CapacitorServer(config.ServerUrl, config.ConfigRoot, Resolutions.None(config.ConfigRoot)), Probe, () => Transport,
                        (_, _, _, _) => new FakeRecorderHousekeeping(), TimeProvider.System, NullLogger<DaemonRecorder>.Instance, _ => Exited);
                });
        }

        public AgentOrchestrator Orch { get; }
        public DaemonConfig Config { get; private set; } = null!;
        public DaemonRecorder Recorder { get; private set; } = null!;
        public RecordingProbe Probe { get; } = new();
        public FakeRecorderTransport Transport { get; } = new() { IsConnected = true };

        public bool Exited { get; set; }

        /// <summary>The daemon's own Claude directory.</summary>
        public string ClaudeHome => new ClaudePaths(Config.Home, null).Home;

        public string Projects => new ClaudePaths(Config.Home, ClaudeHome).Projects;

        public string WatcherDir => WatcherPaths.FromEnvironment(Config.ConfigRoot).Directory;

        public RecorderClaims Claims => new(WatcherDir, TimeProvider.System);

        public IReadOnlyList<RecordedSource> Listed => new RecordedSourceList(Config.Store.StateDirectory(Config.Name), NullLogger.Instance).Load();

        /// <summary>A transcript where Claude keeps one, under the account directory the hook resolved.</summary>
        public string Transcript(string session = Session, string? directory = null) {
            var project = Directory.CreateDirectory(directory ?? Path.Combine(Projects, "-repo")).FullName;
            var path    = Path.Combine(project, $"{session}.jsonl");
            File.WriteAllText(path, """{"type":"user","message":{"content":"Summarise the README"}}""" + "\n");

            return path;
        }

        public RecordTerminalSourceDto Source(string? transcript = null) => new(
            Session, null, transcript ?? Transcript(), "claude", "/repo", false,
            Config.ProfileNameOrNull!, Config.ServerUrl, Config.ConfigRoot.Directory, WatcherDir, ClaudeHome, Claude.Pid, Claude.StartToken);

        public Task<string> HandOverAsync(RecordTerminalSourceDto source) =>
            HandOverAsync(JsonSerializer.Serialize(source, RecordingIpcJsonContext.Default.RecordTerminalSourceDto));

        public async Task<string> HandOverAsync(string payload) {
            using var stream = new MemoryStream();
            await Orch.HandleLocalRecordTerminalSourceAsync(payload, stream, CancellationToken.None);
            stream.Position = 0;

            var ack = await FrameCodec.ReadAsync(stream, CancellationToken.None);

            return ack!.Type == FrameType.RecordSourceAck ? ack.Text : $"unexpected {ack.Type}";
        }

        public async Task<string> FinishAsync(string key) {
            using var stream = new MemoryStream();
            await Orch.HandleLocalFinishSourceAsync(key, stream, CancellationToken.None);
            stream.Position = 0;

            return (await FrameCodec.ReadAsync(stream, CancellationToken.None))!.Text;
        }

        public async ValueTask DisposeAsync() => await Orch.DisposeAsync();
    }

    [Test]
    public async Task A_terminal_hook_of_this_daemons_own_profile_is_recorded_under_a_terminal_claim() {
        await using var f = new Fixture();
        var transcript = f.Transcript();

        await Assert.That(await f.HandOverAsync(f.Source(transcript))).IsEqualTo(RecordingIpc.Recording);

        await WaitHarness.PollUntilAsync(() => f.Transport.Batches.Count > 0);
        await Assert.That(f.Recorder.ActiveKeys).IsEquivalentTo([Session]);
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(f.Claims.IsHosted(Session)).IsFalse();
        await Assert.That(f.Claims.Holder(Session)).IsEqualTo(f.Config.Name);
        await Assert.That(f.Listed.Single()).IsEqualTo(new RecordedSource(null, Session, null, CanonicalPath.Resolve(transcript), "claude", "/repo", false, Process: Claude));
    }

    /// <summary>Two profiles name one daemon, and the hook on the other profile would send to another server. Refused
    /// even for a source this daemon holds, which goes to its own server and not the hook's.</summary>
    [Test]
    public async Task A_hook_on_another_profile_is_refused_by_a_daemon_both_profiles_name() {
        await using var f = new Fixture();
        await f.HandOverAsync(f.Source());

        await Assert.That(await f.HandOverAsync(f.Source() with { Profile = "other" })).IsEqualTo(RecordingIpc.Refused);
    }

    [Test]
    public async Task A_hook_on_another_profile_takes_no_claim() {
        await using var f = new Fixture();

        await Assert.That(await f.HandOverAsync(f.Source() with { Profile = "other" })).IsEqualTo(RecordingIpc.Refused);

        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(f.Recorder.ActiveKeys).IsEmpty();
    }

    [Test]
    public async Task A_hook_sending_to_another_server_is_refused() {
        await using var f = new Fixture();

        await Assert.That(await f.HandOverAsync(f.Source() with { ServerUrl = "https://elsewhere.example" })).IsEqualTo(RecordingIpc.Refused);
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>The same server written another way is the same server.</summary>
    [Test]
    public async Task A_hook_naming_this_daemons_server_another_way_is_recorded() {
        await using var f = new Fixture();

        await Assert.That(await f.HandOverAsync(f.Source() with { ServerUrl = f.Config.ServerUrl.ToUpperInvariant() + "/" })).IsEqualTo(RecordingIpc.Recording);
    }

    /// <summary>Its disable markers and its claims would be read somewhere this daemon never looks, so a watcher it started
    /// would never see this daemon's claim, and a disable it wrote would never stop this daemon.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_hook_reading_other_directories_is_refused(bool configDir) {
        await using var f = new Fixture();
        var elsewhere = Path.Combine(f.Config.Home.Path, "elsewhere");
        var source    = f.Source();

        await Assert.That(await f.HandOverAsync(configDir ? source with { ConfigDir = elsewhere } : source with { WatcherDir = elsewhere })).IsEqualTo(RecordingIpc.Refused);
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task A_transcript_outside_the_claude_projects_the_hook_resolved_is_refused() {
        await using var f = new Fixture();
        var outside = f.Transcript(directory: Path.Combine(f.Config.Home.Path, "notes", "-repo"));

        await Assert.That(await f.HandOverAsync(f.Source(outside))).IsEqualTo(RecordingIpc.Refused);
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>The daemon reads the transcript under the Claude directory the hook names, so that has to be one it would
    /// read itself: its own, or a registered Claude account's.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_hook_naming_a_claude_directory_no_claude_account_has_is_refused(bool registeredForCodex) {
        var accounts = new AccountStore(Tmp.PathTo("accounts"));
        await using var f = new Fixture(accounts: accounts);
        var work = Directory.CreateDirectory(Path.Combine(f.Config.Home.Path, ".claude-work")).FullName;
        if (registeredForCodex) Register(accounts, HarnessId.Codex, work);
        var transcript = f.Transcript(directory: Path.Combine(new ClaudePaths(f.Config.Home, work).Projects, "-repo"));

        await Assert.That(await f.HandOverAsync(f.Source(transcript) with { ClaudeHome = work })).IsEqualTo(RecordingIpc.Refused);
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task A_hook_naming_a_registered_claude_accounts_directory_is_recorded() {
        var accounts = new AccountStore(Tmp.PathTo("accounts"));
        await using var f = new Fixture(accounts: accounts);
        var work = Directory.CreateDirectory(Path.Combine(f.Config.Home.Path, ".claude-work")).FullName;
        Register(accounts, HarnessId.Claude, work);
        var transcript = f.Transcript(directory: Path.Combine(new ClaudePaths(f.Config.Home, work).Projects, "-repo"));

        await Assert.That(await f.HandOverAsync(f.Source(transcript) with { ClaudeHome = work })).IsEqualTo(RecordingIpc.Recording);
    }

    static void Register(AccountStore accounts, HarnessId vendor, string directory) =>
        accounts.Mutate(r => (r with { Accounts = [.. r.Accounts, new VendorAccount($"{vendor}-work", vendor, AccountDirectory.Normalize(directory), "Work", DateTimeOffset.UnixEpoch)] }, 0));

    /// <summary>A link under the projects is how a path outside them reads as inside.</summary>
    [Test]
    public async Task A_transcript_linked_in_from_outside_the_projects_is_refused() {
        await using var f = new Fixture();
        var outside = f.Transcript(directory: Path.Combine(f.Config.Home.Path, "notes", "-repo"));
        Directory.CreateDirectory(f.Projects);
        Directory.CreateSymbolicLink(Path.Combine(f.Projects, "-linked"), Path.GetDirectoryName(outside)!);

        await Assert.That(await f.HandOverAsync(f.Source(Path.Combine(f.Projects, "-linked", $"{Session}.jsonl")))).IsEqualTo(RecordingIpc.Refused);
    }

    /// <summary>The server files what it gets under the session the hook names, so another session's file would land in
    /// the wrong one.</summary>
    [Test]
    public async Task A_transcript_named_for_another_session_is_refused() {
        await using var f = new Fixture();

        await Assert.That(await f.HandOverAsync(f.Source(f.Transcript("fedcba9876543210fedcba9876543210")))).IsEqualTo(RecordingIpc.Refused);
    }

    /// <summary>A source with no live process would have nothing to end it.</summary>
    [Test]
    public async Task A_hook_whose_claude_process_has_exited_is_refused() {
        await using var f = new Fixture();
        var source = f.Source();
        f.Exited = true;

        await Assert.That(await f.HandOverAsync(source)).IsEqualTo(RecordingIpc.Refused);
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task A_disabled_session_is_refused() {
        await using var f = new Fixture();
        var source = f.Source();
        DisabledSessions.Mark(Session, f.Config.ConfigRoot);

        await Assert.That(await f.HandOverAsync(source)).IsEqualTo(RecordingIpc.Refused);
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(f.Recorder.ActiveKeys).IsEmpty();
    }

    [Test]
    public async Task With_recording_off_a_terminal_hook_is_refused_and_nothing_is_claimed() {
        await using var f = new Fixture(on: false);

        await Assert.That(await f.HandOverAsync(f.Source())).IsEqualTo(RecordingIpc.Refused);
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(f.Listed).IsEmpty();
    }

    /// <summary>The server hasn't said whether it can stop a source, so nothing is decided, and the hook starts a watcher.</summary>
    [Test]
    public async Task A_daemon_waiting_on_its_server_says_not_yet() {
        await using var f = new Fixture(serverRecords: null);

        await Assert.That(await f.HandOverAsync(f.Source())).IsEqualTo(RecordingIpc.NotYet);
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task A_second_hook_of_a_held_session_is_answered_as_recording() {
        await using var f = new Fixture();
        var source = f.Source();

        await f.HandOverAsync(source);

        await Assert.That(await f.HandOverAsync(source)).IsEqualTo(RecordingIpc.Recording);
        await Assert.That(f.Recorder.ActiveKeys).IsEquivalentTo([Session]);
    }

    /// <summary>A subagent's transcript is its own source, filed under its session's directory.</summary>
    [Test]
    public async Task A_terminal_subagent_is_recorded_as_its_own_source() {
        await using var f = new Fixture();
        var parent = f.Transcript();
        var child  = ClaudeSubagentFiles.TranscriptPath(parent, "a1b2c3");
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        await File.WriteAllTextAsync(child, "{}\n");

        await f.HandOverAsync(f.Source(parent));
        await Assert.That(await f.HandOverAsync(f.Source(child) with { SubagentId = "a1b2c3" })).IsEqualTo(RecordingIpc.Recording);

        await Assert.That(f.Recorder.ActiveKeys).IsEquivalentTo([Session, $"{Session}-a1b2c3"]);
    }

    /// <summary>A session-end hook finishes the source as it would a hosted one, and the claim goes with it.</summary>
    [Test]
    public async Task A_session_end_finishes_a_terminal_source() {
        await using var f = new Fixture();
        await f.HandOverAsync(f.Source());

        await Assert.That(await f.FinishAsync(Session)).IsEqualTo(RecordingIpc.Finished);

        await Assert.That(f.Recorder.ActiveKeys).IsEmpty();
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(f.Transport.DrainCompletes).IsEqualTo(1);
    }

    /// <summary>A Claude process that exits with no session-end hook still ends its source, as an agent's exit ends a
    /// hosted one. The claim stays through the grace its end waits out, so a late hook still finds the source here.</summary>
    [Test]
    public async Task A_terminal_source_whose_process_exits_is_finished() {
        await using var f = new Fixture();
        await f.HandOverAsync(f.Source());
        await WaitHarness.PollUntilAsync(() => f.Transport.Batches.Count > 0);

        f.Exited = true;
        f.Recorder.FinishExitedProcesses();

        await WaitHarness.PollUntilAsync(() => f.Recorder.ActiveKeys.Count == 0 && f.Transport.DrainCompletes == 1);
        await Assert.That(f.Claims.Read(Session)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(await f.FinishAsync(Session)).IsEqualTo(RecordingIpc.Finished);
    }

    /// <summary>The previous daemon left the source and this one hasn't picked it up: the host would return at once for a
    /// key it doesn't hold, and a finish answered from that would tell the hook a drain happened that didn't.</summary>
    [Test]
    public async Task A_finish_for_a_claimed_source_not_yet_picked_up_is_not_held() {
        await using var f = new Fixture();
        var source = new RecordedSource(null, Session, null, f.Transcript(), "claude", "/repo", false, Process: Claude);
        new RecordedSourceList(f.Config.Store.StateDirectory(f.Config.Name), NullLogger.Instance).TryAdd(source);
        Directory.CreateDirectory(f.WatcherDir);
        f.Claims.TryTake(Session, f.Config.Name, isHosted: false);

        await Assert.That(await f.FinishAsync(Session)).IsEqualTo(RecordingIpc.NotHeld);
        await Assert.That(f.Transport.DrainCompletes).IsEqualTo(0);
    }
}
