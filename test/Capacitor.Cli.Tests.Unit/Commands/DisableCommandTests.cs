using System.Runtime.Versioning;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Workspace;
using Capacitor.Cli.Recording;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>kcap disable</c> tells a person recording has stopped and their data is gone, so what it prints is held to
/// what the daemon and the server actually said.</summary>
public class DisableCommandTests : IDisposable {
    const string Session  = "0123456789abcdef0123456789abcdef";
    const string Subagent = $"{Session}-sub-1";
    const string Holder   = "daemon-a";

    const string Clean = $"Session {Session} disabled. Recording stopped and server data deleted.";

    /// <summary>The daemon this profile would run, which is asked whether or not a claim names it.</summary>
    static string Own => DaemonNameResolver.Resolve([]);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    readonly FakeDaemon _daemon = new();
    readonly FakeWatcherSpawner _spawner = new();
    readonly FakeSessions _sessions = new();
    readonly StringWriter _out = new();
    readonly StringWriter _err = new();

    public void Dispose() {
        _out.Dispose();
        _err.Dispose();
    }

    WatcherPaths Paths => field ??= new(Config.CreateDir("watchers"));

    RecorderClaims Claims => field ??= new(Paths.Directory, _time);

    const string ServerUrl = "http://kcap.example:8080";

    WatcherManager Manager(TimeProvider time) => new(
        Config.Root, Resolutions.At(ServerUrl, Config.Root), new FixedCapacitorHttpClient(), SystemProcessStarter.Instance, Paths, _spawner, time, _daemon, HostedAgent.Terminal);

    Task<int> Run(TimeProvider? time = null) => RunOn(Session, time);

    Task<int> RunOn(string session, TimeProvider? time = null, HostedAgent? hosted = null, Func<string, string?>? getEnv = null, TextWriter? stdout = null) =>
        new DisableCommand(Config.Root, Manager(time ?? _time), hosted ?? HostedAgent.Terminal) { GetEnv = getEnv ?? (_ => null) }.Handle(session, Api, stdout ?? _out, _err);

    ISessionsApi Api() => _sessions.Unusable is { } unusable ? throw unusable : _sessions;

    /// <summary>A session the agent was running before it moved to <see cref="Session"/>.</summary>
    const string Earlier = "fedcba9876543210fedcba9876543210";

    string Printed => _out.ToString().Trim();

    void Claim(string key, string holder = Holder) => Claims.TryTake(key, holder, isHosted: true);

    /// <summary>A real process behind a pid file, so a signal shows in whether it is still alive.</summary>
    async Task<DummyProcess> WatcherOn(string key) {
        var process = DummyProcess.StartSleep(30);
        await File.WriteAllTextAsync(Paths.PidFile(key), $"{process.Pid}\n{PidIdentity.Capture(process.Pid)}");

        return process;
    }

    /// <summary>With no claim there is nothing a daemon could add: a watcher and a subagent's watcher stop, the session is
    /// marked, the server is told, and no daemon is so much as asked. On the real clock, because the watcher's stop waits
    /// for it on a platform with no signal to send.</summary>
    [Test]
    [ParallelLimiter<SubprocessLimit>]
    public async Task Disable_with_no_daemon_behaves_as_today() {
        using var watcher  = await WatcherOn(Session);
        using var subagent = await WatcherOn(Subagent);

        await Assert.That(await Run(TimeProvider.System)).IsEqualTo(0);

        await Assert.That(watcher.HasExited).IsTrue();
        await Assert.That(subagent.HasExited).IsTrue();
        await Assert.That(DisabledSessions.IsDisabled(Session, Config.Root)).IsTrue();
        await Assert.That(_sessions.Deleted).IsEquivalentTo([Session]);
        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>A hosted session on a watcher has no claim, and only its daemon can say a terminal is still going, so this
    /// profile's daemon hears of every disable. No answer from it, with no claim, says nothing about recording.</summary>
    [Test]
    public async Task With_no_claim_only_this_profiles_daemon_is_asked_and_no_answer_changes_nothing() {
        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(_daemon.Asked).IsEquivalentTo([(Own, Session)]);
        await Assert.That(Printed).IsEqualTo(Clean);
    }

    [Test]
    public async Task Disable_with_nothing_found_on_the_server_says_so() {
        _sessions.Answer = new DeleteSessionResponse.NotFound();

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo($"Session {Session} disabled. No server data found (may have already been deleted).");
    }

    [Test]
    public async Task Disable_with_no_server_is_no_failure() {
        _sessions.Failure = new CapacitorApiException(null, "The server could not be reached.");

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo("Session disabled locally (recording stopped, hooks silenced). Server data not deleted.");
    }

    [Test]
    public async Task Disable_refused_by_the_server_fails() {
        _sessions.Failure = new CapacitorApiException(403, "Forbidden.");

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(_err.ToString()).Contains("Forbidden.");
        await Assert.That(Printed).IsEmpty();
    }

    /// <summary>A hook or a launch racing the command reads the marker, so it has to be down before the daemon is told to
    /// let go, and the server's deletion comes after the stop or a send could land behind it.</summary>
    [Test]
    public async Task Disable_marks_first_and_deletes_last() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Stopped);
        bool? markedWhenAsked = null;
        bool? askedWhenDeleted = null;
        _daemon.OnAsked = () => markedWhenAsked = DisabledSessions.IsDisabled(Session, Config.Root);
        _sessions.OnDelete = () => askedWhenDeleted = _daemon.Asked.Count > 0;

        await Run();

        await Assert.That(markedWhenAsked).IsTrue();
        await Assert.That(askedWhenDeleted).IsTrue();
    }

    /// <summary>The terminal has no daemon named in its environment: the claim says which one holds the source.</summary>
    [Test]
    public async Task Disable_asks_the_daemon_the_claim_names() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Stopped);

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(_daemon.Asked).Contains((Holder, Session));
        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>A pid scan never sees a daemon's child: the claim file is all it leaves here. The parent's source may have
    /// ended long ago, so the child's claim alone has to bring the daemon in.</summary>
    [Test]
    public async Task Disable_finds_a_daemon_held_subagent() {
        Claim(Subagent);
        _daemon.Answer = new(RecordingDisable.Stopped);

        await Run();

        await Assert.That(_daemon.Asked).Contains((Holder, Session));
        await Assert.That(Claims.Read(Subagent)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task Disable_asks_each_daemon_once() {
        Claim(Session);
        Claim(Subagent);
        Claim($"{Session}-sub-2", "daemon-b");
        _daemon.Answer = new(RecordingDisable.Stopped);

        await Run();

        await Assert.That(_daemon.Asked).IsEquivalentTo([(Holder, Session), ("daemon-b", Session), (Own, Session)]);
    }

    /// <summary>No claim of another session is touched, a longer id with this one as its prefix included.</summary>
    [Test]
    public async Task Disable_leaves_another_sessions_claim_alone() {
        const string other = "fedcba9876543210fedcba9876543210";
        Claim(other);
        Claim($"{other}-sub-1");

        await Run();

        await Assert.That(Claims.Read(other)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(Claims.Read($"{other}-sub-1")).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(_daemon.Asked).DoesNotContain((Holder, Session));
    }

    /// <summary>A claim going is what stops a recorder at its next tick, so it goes whatever the daemon said: an older or
    /// a hung daemon still ends up stopping.</summary>
    [Test]
    [Arguments(RecordingDisable.Stopped)]
    [Arguments(RecordingDisable.SendInFlight)]
    [Arguments(RecordingDisable.NotHeld)]
    [Arguments(RecordingDisable.StillStopping)]
    [Arguments(RecordingDisable.Silent)]
    [Arguments(RecordingDisable.NoSocket)]
    public async Task Disable_lets_go_of_the_claim_whatever_the_daemon_said(RecordingDisable answer) {
        Claim(Session);
        _daemon.Answer = new(answer);

        await Run();

        await Assert.That(Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>A claim it holds no recorder behind: a restart that hasn't got to it, which the marker now refuses.</summary>
    [Test]
    public async Task A_daemon_holding_none_of_it_is_a_clean_stop() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.NotHeld);

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>A batch the server hadn't answered can land behind the deletion, and a second delete can answer 404 with the
    /// stream revived, so all the command can say is what is true: it may have landed.</summary>
    [Test]
    public async Task Disable_says_so_when_a_send_may_have_landed_and_promises_no_cure() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.SendInFlight);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).StartsWith($"Session {Session} disabled. Server data deleted.");
        await Assert.That(Printed).DoesNotContain("Recording stopped");
        await Assert.That(Printed).Contains("may still have landed");
        await Assert.That(Printed).DoesNotContain("To delete");
    }

    const string EarlierDeletion = "An earlier kcap disable deleted this session before it could confirm recording had stopped.";

    /// <summary>What landed behind that deletion stays out of reach from here whatever a later run finds, so every later
    /// run says so, and the same way however its own daemons answer.</summary>
    [Test]
    [Arguments(RecordingDisable.SendInFlight)]
    [Arguments(RecordingDisable.StillStopping)]
    [Arguments(RecordingDisable.Silent)]
    public async Task Every_later_run_after_an_unconfirmed_deletion_says_so(RecordingDisable first) {
        Claim(Session);
        _daemon.Answer = new(first);
        await Run();
        _daemon.Answer = new(RecordingDisable.NoSocket);

        foreach (var _ in Enumerable.Range(0, 2)) {
            _out.GetStringBuilder().Clear();

            await Assert.That(await Run()).IsEqualTo(1);

            await Assert.That(Printed).StartsWith(Clean);
            await Assert.That(Printed).Contains(EarlierDeletion);
        }
    }

    /// <summary>The deletion that ran is the one this run's own caveat already speaks for.</summary>
    [Test]
    public async Task The_run_that_deleted_unconfirmed_does_not_call_itself_earlier() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.SendInFlight);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).DoesNotContain(EarlierDeletion);
    }

    /// <summary>A watcher left running is as unconfirmed as a daemon that didn't answer.</summary>
    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task A_later_run_after_a_deletion_that_left_a_watcher_says_so() {
        await File.WriteAllTextAsync(Paths.PidFile(Session), "1\n");
        File.SetUnixFileMode(Paths.PidFile(Session), UnixFileMode.None);
        await Run();
        File.Delete(Paths.PidFile(Session));
        _out.GetStringBuilder().Clear();

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).Contains(EarlierDeletion);
    }

    /// <summary>Only a deletion that ran can be overtaken. A silent daemon is this run's caveat and nothing is kept of it.</summary>
    [Test]
    [Arguments("unreachable")]
    [Arguments("refused")]
    public async Task An_unconfirmed_stop_with_no_deletion_leaves_nothing_behind(string server) {
        _daemon.Names = ["daemon-b"];
        _daemon.Answers["daemon-b"] = new(RecordingDisable.Silent);
        if (server == "unreachable") _sessions.Failure = new CapacitorApiException(null, "The server could not be reached.");
        if (server == "refused") _sessions.Failure = new CapacitorApiException(500, "Server error.");
        await Run();
        _out.GetStringBuilder().Clear();
        _daemon.Answers["daemon-b"] = new(RecordingDisable.NoSocket);
        _sessions.Failure = null;
        _sessions.Answer = new DeleteSessionResponse.Deleted();

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>A send still on its way can create the session behind a deletion that found nothing, so that deletion is
    /// doubted later as one that deleted would be.</summary>
    [Test]
    public async Task An_unconfirmed_stop_whose_deletion_found_nothing_is_doubted_later() {
        _daemon.Names = ["daemon-b"];
        _daemon.Answers["daemon-b"] = new(RecordingDisable.Silent);
        _sessions.Answer = new DeleteSessionResponse.NotFound();
        await Run();
        _out.GetStringBuilder().Clear();
        _daemon.Answers["daemon-b"] = new(RecordingDisable.NoSocket);
        _sessions.Answer = new DeleteSessionResponse.Deleted();

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).StartsWith(Clean);
        await Assert.That(Printed).Contains(EarlierDeletion);
    }

    /// <summary>A live agent's output isn't recording, and its own line says what goes on.</summary>
    [Test]
    public async Task A_deletion_with_a_live_agent_leaves_nothing_behind() {
        _daemon.Answer = new(RecordingDisable.NotHeld, AgentLive: true);
        await Run();
        _out.GetStringBuilder().Clear();
        _daemon.Answer = new(RecordingDisable.NotHeld);

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>A clean run left nothing to doubt, so running it twice stays clean.</summary>
    [Test]
    public async Task A_second_run_behind_a_clean_run_stays_clean() {
        await Run();
        _out.GetStringBuilder().Clear();

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    const string StillRunning = "This agent is still running, and its live output keeps reaching the server until it ends. Stop the agent to end it.";

    /// <summary>A live agent's output isn't recording and isn't stopped, whichever runtime carries it, so "Recording stopped"
    /// would be untrue of what a person most needs to know. It says so and goes no further: nothing here failed.</summary>
    [Test]
    public async Task Disable_says_so_when_a_live_agent_keeps_sending() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Stopped, AgentLive: true);

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).StartsWith($"Session {Session} disabled. Server data deleted.");
        await Assert.That(Printed).Contains(StillRunning);
    }

    [Test]
    public async Task Disable_says_nothing_of_an_agent_with_none_live() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Stopped);

        await Run();

        await Assert.That(Printed).DoesNotContain("still running");
    }

    /// <summary>A hook writes the new session over the agent's claim when the agent moves to it, and the daemon may not
    /// have heard: its recorder could be off, or it may not answer. The claim is what says so.</summary>
    [Test]
    public async Task A_session_a_hosted_agent_moved_to_says_its_output_goes_on() {
        var sessions = AgentSessions.OnThisMachine(Config.Root);
        var hosted   = new ClaimOrigin("claude", ServerUrl, null, null, "agent-1");
        sessions.Claim(Environment.ProcessId, SessionId.Parse(Earlier)!, hosted);
        sessions.Claim(Environment.ProcessId, SessionId.Parse(Session)!, hosted);

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).Contains(StillRunning);
        await Assert.That(Printed).DoesNotContain("Recording stopped");
    }

    /// <summary>The agent has left this session, and a person told it is still going would stop the wrong thing.</summary>
    [Test]
    public async Task A_session_a_hosted_agent_moved_off_says_nothing_of_it() {
        var sessions = AgentSessions.OnThisMachine(Config.Root);
        var hosted   = new ClaimOrigin("claude", ServerUrl, null, null, "agent-1");
        sessions.Claim(Environment.ProcessId, SessionId.Parse(Earlier)!, hosted);
        sessions.Claim(Environment.ProcessId, SessionId.Parse(Session)!, hosted);

        await RunOn(Earlier);

        await Assert.That(Printed).DoesNotContain("still running");
    }

    /// <summary>A person's own terminal session is claimed the same way and has nothing that outlives its hooks, and a
    /// private agent has no agent id and no mirror.</summary>
    [Test]
    public async Task A_session_with_no_hosted_agent_claimed_reads_clean() {
        AgentSessions.OnThisMachine(Config.Root).Claim(Environment.ProcessId, SessionId.Parse(Session)!, new ClaimOrigin("claude", ServerUrl, null, null));

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>Run from inside the agent, the environment says it is live whatever its daemon answers.</summary>
    [Test]
    public async Task Inside_a_hosted_agent_its_own_session_says_its_output_goes_on() {
        var hosted = new HostedAgent("agent-1", false, DaemonBridge.None);

        await RunOn(Session, hosted: hosted, getEnv: name => name == "KCAP_SESSION_ID" ? Session : null);

        await Assert.That(Printed).Contains(StillRunning);
    }

    [Test]
    public async Task Inside_a_hosted_agent_another_session_says_nothing_of_it() {
        var hosted = new HostedAgent("agent-1", false, DaemonBridge.None);

        await RunOn(Session, hosted: hosted, getEnv: name => name == "KCAP_SESSION_ID" ? Earlier : null);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>A live agent can sit on a daemon of any profile with no claim to name it, on a watcher or on ACP.</summary>
    [Test]
    public async Task Every_daemon_on_the_machine_is_asked_and_a_live_agent_on_any_of_them_is_said() {
        _daemon.Names = ["daemon-b", "daemon-c"];
        _daemon.Answers["daemon-c"] = new(RecordingDisable.NotHeld, AgentLive: true);

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(_daemon.Asked.Select(asked => asked.Daemon)).IsEquivalentTo(new[] { Own, "daemon-b", "daemon-c" });
        await Assert.That(Printed).Contains(StillRunning);
    }

    /// <summary>A daemon with no socket has nothing running.</summary>
    [Test]
    public async Task A_daemon_with_no_socket_changes_nothing() {
        _daemon.Names = ["daemon-b"];
        _daemon.Answers["daemon-b"] = new(RecordingDisable.NoSocket);

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>One that takes the connection and goes quiet may host this very session.</summary>
    [Test]
    public async Task A_silent_daemon_is_not_a_clean_stop() {
        _daemon.Names = ["daemon-b"];
        _daemon.Answers["daemon-b"] = new(RecordingDisable.Silent);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).StartsWith($"Session {Session} disabled. Server data deleted.");
        await Assert.That(Printed).Contains("A kcap daemon on this machine did not answer");
    }

    /// <summary>Every daemon gets the same short wait when no claim says one of them has a stop to finish.</summary>
    [Test]
    public async Task Silent_daemons_cost_one_bound_between_them() {
        _daemon.Names = ["daemon-b", "daemon-c", "daemon-d"];
        _daemon.Hangs = true;

        var run = Run();
        await _daemon.Reached.Task.WithinDeadlineAsync("the frame");
        _time.Advance(WatcherManager.HandOffBudget);

        await Assert.That(await run.WithinDeadlineAsync("the command")).IsEqualTo(1);
    }

    [Test]
    public async Task A_daemon_directory_that_cannot_be_read_is_not_a_clean_stop() {
        _daemon.Names = null;

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).Contains("could not read what records this session");
    }

    /// <summary>A watcher that outlives the attempt keeps sending, and a stop that leaves it must not say otherwise. The pid
    /// file can't be read here, which is how a stop ends up keeping it.</summary>
    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task A_watcher_that_cannot_be_stopped_is_not_a_clean_stop() {
        await File.WriteAllTextAsync(Paths.PidFile(Session), "1\n");
        File.SetUnixFileMode(Paths.PidFile(Session), UnixFileMode.None);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).StartsWith($"Session {Session} disabled. Server data deleted.");
        await Assert.That(Printed).Contains("A watcher recording this session could not be stopped");
    }

    /// <summary>What the source was doing isn't known, so this can't call it a batch.</summary>
    [Test]
    public async Task A_source_that_has_not_ended_is_not_called_a_batch() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.StillStopping);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).StartsWith($"Session {Session} disabled. Server data deleted.");
        await Assert.That(Printed).Contains("still stopping");
        await Assert.That(Printed).DoesNotContain("batch");
    }

    /// <summary>A profile with no usable server still stops recording, and what that stop found is worth as much as ever.
    /// The exit is the one an unusable URL has everywhere.</summary>
    [Test]
    public async Task An_unusable_server_url_keeps_the_caveats_and_the_live_line() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Silent, AgentLive: true);
        _sessions.Unusable = new UnusableServerUrlException("Fix the URL.");

        await Assert.That(await Run()).IsEqualTo(2);

        await Assert.That(_err.ToString()).Contains("Fix the URL.");
        await Assert.That(Printed).StartsWith("Session disabled locally (hooks silenced). Server data not deleted.");
        await Assert.That(Printed).Contains("did not confirm");
        await Assert.That(Printed).Contains(StillRunning);
    }

    /// <summary>A claim names a daemon of any profile, and that daemon stops. The deletion only reaches this profile's
    /// server, so everything the other one holds is still there whatever this one answered.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Disable_says_so_when_the_session_went_to_another_server(bool found) {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Stopped, Server: "https://other.example");
        if (!found) _sessions.Answer = new DeleteSessionResponse.NotFound();

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).Contains("https://other.example");
    }

    /// <summary>A daemon under another config root has no claim here, and its answer is all that says it held the session
    /// or hosts its agent, on a server this run never reaches.</summary>
    [Test]
    [Arguments(RecordingDisable.Stopped, false)]
    [Arguments(RecordingDisable.SendInFlight, false)]
    [Arguments(RecordingDisable.StillStopping, false)]
    [Arguments(RecordingDisable.NotHeld, true)]
    public async Task A_daemon_with_the_session_on_another_server_says_so_with_no_claim(RecordingDisable outcome, bool agentLive) {
        _daemon.Names = ["daemon-b"];
        _daemon.Answers["daemon-b"] = new(outcome, AgentLive: agentLive, Server: "https://b.example");

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).StartsWith($"Session {Session} disabled. Server data deleted.");
        await Assert.That(Printed).Contains("sends to https://b.example, and nothing was deleted there");
    }

    /// <summary>Each daemon's server holds a copy this deletion never reaches, so every one is named and kept for later
    /// runs, and one server two daemons spell differently is named once.</summary>
    [Test]
    public async Task Every_other_server_the_daemons_name_is_said_and_kept() {
        _daemon.Names = ["daemon-b", "daemon-c", "daemon-d"];
        _daemon.Answers["daemon-b"] = new(RecordingDisable.Stopped, Server: "https://b.example");
        _daemon.Answers["daemon-c"] = new(RecordingDisable.NotHeld, AgentLive: true, Server: "https://c.example");
        _daemon.Answers["daemon-d"] = new(RecordingDisable.Stopped, Server: "HTTPS://B.EXAMPLE:443/");

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).Contains("sends to https://b.example, and nothing was deleted there");
        await Assert.That(Printed).Contains("sends to https://c.example, and nothing was deleted there");
        await Assert.That(Printed.Split("nothing was deleted there").Length - 1).IsEqualTo(2);
        await Assert.That(DisabledSessions.OtherServers(Session, Config.Root)).IsEquivalentTo(["https://b.example", "https://c.example"]);
    }

    const string OtherServer = "https://other.example";

    const string FoundEarlier = $"An earlier kcap disable found a daemon sending this session to {OtherServer}. This command can't delete data there";

    /// <summary>The first run lets go of the claims, so the daemon that sent the session elsewhere holds none of it the next
    /// time and says nothing of that server. The data there is still there, and only the marker remembers it.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Every_later_run_after_one_found_another_server_names_it(bool found) {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Stopped, Server: OtherServer);
        await Run();
        _daemon.Answer = new(RecordingDisable.NotHeld, Server: OtherServer);
        if (!found) _sessions.Answer = new DeleteSessionResponse.NotFound();

        foreach (var _ in Enumerable.Range(0, 2)) {
            _out.GetStringBuilder().Clear();

            await Assert.That(await Run()).IsEqualTo(1);

            await Assert.That(Printed).StartsWith(found ? Clean : $"Session {Session} disabled. No server data found");
            await Assert.That(Printed).Contains(FoundEarlier);
        }
    }

    /// <summary>A run that finds the server itself says so in its own line, the first run and any after it alike.</summary>
    [Test]
    public async Task A_run_that_finds_the_other_server_itself_names_it_once() {
        _daemon.Answer = new(RecordingDisable.Stopped, Server: OtherServer);

        foreach (var _ in Enumerable.Range(0, 2)) {
            _out.GetStringBuilder().Clear();
            Claim(Session);

            await Assert.That(await Run()).IsEqualTo(1);

            await Assert.That(Printed).Contains($"sends to {OtherServer}, and nothing was deleted there");
            await Assert.That(Printed).DoesNotContain("An earlier kcap disable found");
        }
    }

    /// <summary>Only what this run keeps can be named by the next, so a run that couldn't keep the server says so.</summary>
    [Test]
    public async Task A_server_that_cannot_be_kept_for_later_runs_is_said() {
        Config.CreateDir("disabled", $"{Session}.other-server");
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Stopped, Server: OtherServer);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).Contains($"kcap could not record that a daemon sends this session to {OtherServer}");
        await Assert.That(Printed).Contains("so a later kcap disable won't name that server.");
    }

    /// <summary>Only what this run keeps can be said by the next, so a run that couldn't keep its unconfirmed deletion says
    /// so. The marker is there already and can't be written, which leaves the mark itself standing.</summary>
    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task An_unconfirmed_deletion_that_cannot_be_kept_for_later_runs_is_said() {
        File.SetUnixFileMode(Config.CreateFile(Path.Combine("disabled", Session)), UnixFileMode.UserRead);
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.SendInFlight);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).DoesNotContain("could not mark");
        await Assert.That(Printed).Contains("kcap could not record that this deletion ran before recording was confirmed stopped");
        await Assert.That(Printed).Contains("so a later kcap disable won't say so.");
    }

    /// <summary>A stdout that has gone away fails the deletion's line, and what later runs speak from is kept all the same.</summary>
    [Test]
    public async Task What_later_runs_need_is_kept_ahead_of_a_line_that_cannot_be_printed() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.SendInFlight, Server: OtherServer);

        await Assert.That(async () => await RunOn(Session, stdout: new ClosedPipeWriter())).Throws<IOException>();

        await Assert.That(DisabledSessions.WasDeletedUnconfirmed(Session, Config.Root)).IsTrue();
        await Assert.That(DisabledSessions.OtherServers(Session, Config.Root)).IsEquivalentTo([OtherServer]);
    }

    /// <summary>One that holds none of it and hosts nothing says nothing about where the session went.</summary>
    [Test]
    public async Task A_daemon_of_another_server_with_none_of_it_says_nothing() {
        _daemon.Names = ["daemon-b"];
        _daemon.Answers["daemon-b"] = new(RecordingDisable.NotHeld, Server: "https://b.example");

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>With no claim the daemon is asked all the same, or a hosted session on a watcher, and every ACP session,
    /// would never hear of this.</summary>
    [Test]
    public async Task Disable_mentions_a_live_agent_with_no_claim() {
        _daemon.Answer = new(RecordingDisable.NotHeld, AgentLive: true, Server: ServerUrl);

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).Contains(StillRunning);
        await Assert.That(Printed).DoesNotContain("sends to");
    }

    [Test]
    public async Task A_daemon_on_this_server_is_no_other_server() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Stopped, Server: "HTTP://KCAP.EXAMPLE:8080/");

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    string LifecycleSpool => new HookSpool(Config.Root, _time, ServerUrl).DirectoryPath;

    string WorkspaceSpoolDirectory => new WorkspaceSpool(Config.Root, ServerUrl, _time).DirectoryPath;

    /// <summary>Moves the clock on until <paramref name="run"/> is done, for a command waiting on it.</summary>
    async Task<int> Advancing(Task<int> run) {
        while (!run.IsCompleted) {
            _time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(5);
        }

        return await run;
    }

    /// <summary>A drain pass can read the marker a moment before it goes down and have a post of the session on its way.
    /// Every pass holds its spool's lease throughout, so the deletion waits for that lease.</summary>
    [Test]
    [Arguments("lifecycle")]
    [Arguments("workspace")]
    public async Task The_deletion_waits_for_a_spool_drain_under_way(string spool) {
        var released = false;
        bool? deletedWhileDraining = null;
        _sessions.OnDelete = () => deletedWhileDraining = !Volatile.Read(ref released);
        var pass = SpoolDrainLease.TryTake(spool == "lifecycle" ? LifecycleSpool : WorkspaceSpoolDirectory)!;

        var run = Run();
        await Task.Delay(100);
        Volatile.Write(ref released, true);
        pass.Dispose();

        await Assert.That(await Advancing(run).WithinDeadlineAsync("the command")).IsEqualTo(0);

        await Assert.That(deletedWhileDraining).IsFalse();
        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>Past the wait the pass may still be sending, so the deletion goes ahead and says so.</summary>
    [Test]
    public async Task A_spool_drain_that_outlasts_the_wait_is_not_a_clean_stop() {
        using var pass = SpoolDrainLease.TryTake(LifecycleSpool);

        await Assert.That(await Advancing(Run()).WithinDeadlineAsync("the command")).IsEqualTo(1);

        await Assert.That(_sessions.Deleted).IsEquivalentTo([Session]);
        await Assert.That(Printed).StartsWith($"Session {Session} disabled. Server data deleted.");
        await Assert.That(Printed).Contains("so a send of this session may still land");
        await Assert.That(DisabledSessions.WasDeletedUnconfirmed(Session, Config.Root)).IsTrue();
    }

    sealed class Answering(Func<HttpResponseMessage> answer) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer());
    }

    /// <summary>A drain pass over this profile's spool, its posts answered by <paramref name="answer"/>.</summary>
    async Task DrainPassAsync(Func<HttpResponseMessage> answer) {
        using var client = new HttpClient(new Answering(answer));

        await LifecycleSpoolDrain.RunAsync(
            Config.Root, new Core.Harness.Cursor.CursorMarkers(Config.Root, _time), client, ServerUrl, new HookSpool(Config.Root, _time, ServerUrl),
            new TranscriptSpool(Config.Root, _time, ServerUrl), currentSessionId: null, TimeSpan.FromSeconds(30), _time, CancellationToken.None);
    }

    void SpoolSessionStart() =>
        new HookSpool(Config.Root, _time, ServerUrl).Append(Session, "session-start/codex", $$"""{"session_id":"{{Session}}"}""");

    /// <summary>A pass that has ended can have left a post the server never answered, which it may still be taking in
    /// and which could land behind the deletion. A later post's answer says nothing of that one.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_spooled_post_the_server_never_answered_is_not_a_clean_stop(bool deliveredLater) {
        SpoolSessionStart();
        await DrainPassAsync(() => throw new TaskCanceledException("The post timed out."));
        if (deliveredLater) await DrainPassAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).StartsWith($"Session {Session} disabled. Server data deleted.");
        await Assert.That(Printed).Contains("has had no answer from the server");
        await Assert.That(DisabledSessions.WasDeletedUnconfirmed(Session, Config.Root)).IsTrue();
        await Assert.That(UnansweredPosts.Has(LifecycleSpool, Session)).IsFalse();
    }

    /// <summary>Nothing found says nothing of a post the server may still be taking in, which can bring the session back
    /// behind this run, so the doubt is kept for later runs as an unconfirmed deletion's is.</summary>
    [Test]
    public async Task A_spooled_post_with_no_answer_behind_nothing_found_is_kept_for_later_runs() {
        SpoolSessionStart();
        await DrainPassAsync(() => throw new TaskCanceledException("The post timed out."));
        _sessions.Answer = new DeleteSessionResponse.NotFound();

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).Contains("has had no answer from the server");
        await Assert.That(DisabledSessions.WasDeletedUnconfirmed(Session, Config.Root)).IsTrue();
        await Assert.That(UnansweredPosts.Has(LifecycleSpool, Session)).IsFalse();
    }

    /// <summary>The records are the only trace of the doubt until it is kept, so they stay when it can't be, and the next
    /// run says it again.</summary>
    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task Posts_with_no_answer_stay_recorded_when_the_doubt_cannot_be_kept() {
        SpoolSessionStart();
        await DrainPassAsync(() => throw new TaskCanceledException("The post timed out."));
        File.SetUnixFileMode(Config.CreateFile(Path.Combine("disabled", Session)), UnixFileMode.UserRead);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(UnansweredPosts.Has(LifecycleSpool, Session)).IsTrue();
        await Assert.That(Printed).DoesNotContain("so a later kcap disable won't say so");
    }

    /// <summary>An error status is an answer: the server has finished with that post.</summary>
    [Test]
    public async Task A_spooled_post_the_server_answered_with_an_error_is_a_clean_stop() {
        SpoolSessionStart();
        await DrainPassAsync(() => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
    }

    /// <summary>A failed marker can't be a reason to leave recording running, so what can be stopped still is. The person
    /// has to hear of it, because the marker is what keeps a restart or a hook from bringing the session back.</summary>
    [Test]
    public async Task A_marker_that_cannot_be_written_still_stops_recording_and_says_so() {
        await File.WriteAllTextAsync(Config.Root.Path("disabled"), "in the way of the directory");
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Stopped);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(_daemon.Asked).Contains((Holder, Session));
        await Assert.That(_sessions.Deleted).IsEquivalentTo([Session]);
        await Assert.That(Printed).StartsWith($"Server data for session {Session} deleted.");
        await Assert.That(Printed).Contains("could not mark");
    }

    /// <summary>A daemon's agents' hooks read the daemon's own marker, so one it couldn't write leaves them free to record
    /// the session again, behind the deletion.</summary>
    [Test]
    public async Task A_daemon_that_could_not_mark_the_session_is_not_a_clean_stop() {
        Claim(Session);
        _daemon.Answer = new(RecordingDisable.Stopped, MarkFailed: true);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).StartsWith($"Session {Session} disabled. Server data deleted.");
        await Assert.That(Printed).Contains("A kcap daemon could not mark this session disabled");
        await Assert.That(DisabledSessions.WasDeletedUnconfirmed(Session, Config.Root)).IsTrue();
    }

    /// <summary>Every line ahead of the caveat, in every state the server can leave.</summary>
    [Test]
    [Arguments("deleted")]
    [Arguments("not found")]
    [Arguments("unreachable")]
    public async Task A_marker_that_cannot_be_written_is_not_called_disabled(string server) {
        await File.WriteAllTextAsync(Config.Root.Path("disabled"), "in the way of the directory");
        if (server == "not found") _sessions.Answer = new DeleteSessionResponse.NotFound();
        if (server == "unreachable") _sessions.Failure = new CapacitorApiException(null, "The server could not be reached.");

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).DoesNotContain("disabled.");
        await Assert.That(Printed).DoesNotContain("silenced");
        await Assert.That(Printed).DoesNotContain("Recording stopped");
    }

    /// <summary>Unreadable reads as no claim otherwise, and that is the clean line over a session that may be recording.</summary>
    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task Claims_that_cannot_be_read_are_not_a_clean_stop() {
        var watchers = Paths.Directory;
        Claim(Session);
        var manager = Manager(_time);
        File.SetUnixFileMode(watchers, UnixFileMode.None);

        try {
            var code = await new DisableCommand(Config.Root, manager, HostedAgent.Terminal) { GetEnv = _ => null }.Handle(Session, () => _sessions, _out, _err);

            await Assert.That(code).IsEqualTo(1);
            await Assert.That(Printed).DoesNotContain("Recording stopped");
            await Assert.That(Printed).Contains("could not read");
        } finally {
            File.SetUnixFileMode(watchers, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Finding the session's own watcher needs no listing, so a directory that can't be listed still has it
    /// stopped. On the real clock, because the stop waits for the watcher to exit.</summary>
    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    [ParallelLimiter<SubprocessLimit>]
    public async Task A_watcher_directory_that_cannot_be_listed_still_stops_the_sessions_watcher() {
        using var watcher = await WatcherOn(Session);
        var watchers = Paths.Directory;
        File.SetUnixFileMode(watchers, UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try {
            await Assert.That(await Run(TimeProvider.System)).IsEqualTo(1);

            await Assert.That(watcher.HasExited).IsTrue();
            await Assert.That(Printed).Contains("could not read what records this session");
        } finally {
            File.SetUnixFileMode(watchers, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Nothing here knows recording stopped, so nothing printed may say it did.</summary>
    [Test]
    public async Task Disable_says_so_when_the_daemon_does_not_answer() {
        Claim(Session);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).DoesNotContain("Recording stopped");
        await Assert.That(Printed).Contains("did not confirm");
        await Assert.That(_sessions.Deleted).IsEquivalentTo([Session]);
    }

    /// <summary>The bound holds with a daemon that takes the frame and goes quiet, and the command still deletes.</summary>
    [Test]
    public async Task Disable_gives_up_on_a_daemon_that_hangs() {
        Claim(Session);
        _daemon.Hangs = true;

        var run = Run();
        await _daemon.Reached.Task.WithinDeadlineAsync("the frame");
        _time.Advance(WatcherManager.DisableBudget);

        await Assert.That(await run.WithinDeadlineAsync("the command")).IsEqualTo(1);
        await Assert.That(Printed).Contains("did not confirm");
    }

    [Test]
    public async Task Disable_without_a_server_says_so_when_the_daemon_does_not_answer() {
        Claim(Session);
        _sessions.Failure = new CapacitorApiException(null, "The server could not be reached.");

        await Run();

        await Assert.That(Printed).DoesNotContain("recording stopped");
        await Assert.That(Printed).Contains("Server data not deleted.");
        await Assert.That(Printed).Contains("did not confirm");
    }

    [Test]
    public async Task Disable_with_nothing_on_the_server_says_so_when_the_daemon_does_not_answer() {
        Claim(Session);
        _sessions.Answer = new DeleteSessionResponse.NotFound();

        await Run();

        await Assert.That(Printed).Contains("No server data found");
        await Assert.That(Printed).Contains("did not confirm");
    }

    /// <summary>A stale claim is a daemon that stopped beating, so with nobody answering nothing is recording. It comes back
    /// to a marker and refuses the source.</summary>
    [Test]
    public async Task A_stale_claim_nobody_answers_for_is_a_clean_stop() {
        Claim(Session);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Assert.That(await Run()).IsEqualTo(0);

        await Assert.That(Printed).IsEqualTo(Clean);
        await Assert.That(Claims.Read(Session)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>A corrupt claim is still a claim, and with no name in it there is nobody to ask.</summary>
    [Test]
    public async Task A_claim_that_names_nobody_is_not_a_clean_stop() {
        Claim(Session);
        await File.WriteAllTextAsync(Path.Combine(Paths.Directory, $"{Session}.claim"), "garbage");
        Claims.Beat(Session);

        await Assert.That(await Run()).IsEqualTo(1);

        await Assert.That(Printed).Contains("did not confirm");
    }

    /// <summary>A hand-off started ahead of the disable runs on by itself. The transcript here is missing, which fails a
    /// generation that goes ahead, so the marker is what ends this one cleanly.</summary>
    [Test]
    public async Task A_title_handed_off_ahead_of_a_disable_is_dropped() {
        using var home = new TempHome();
        DisabledSessions.Mark(Session, Config.Root);
        var command = new GenerateTitleCommand(Config.Root, Resolutions.None(Config.Root), TestHarnesses.Under(home), new FixedCapacitorHttpClient(), TimeProvider.System);

        await Assert.That(await command.GenerateAsync("http://127.0.0.1:1", Session, "claude", Config.Root.Path("no-such-transcript.jsonl"))).IsEqualTo(0);
    }

    /// <summary>A stdout whose reader has gone away.</summary>
    sealed class ClosedPipeWriter : TextWriter {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => throw new IOException("Broken pipe");

        public override void Write(string? value) => throw new IOException("Broken pipe");
    }

    sealed class FakeSessions : ISessionsApi {
        readonly List<string> _deleted = [];

        public DeleteSessionResponse Answer { get; set; } = new DeleteSessionResponse.Deleted();

        public CapacitorApiException? Failure { get; set; }

        /// <summary>Thrown resolving the API at all, as a profile with no usable server does.</summary>
        public UnusableServerUrlException? Unusable { get; set; }

        public Action? OnDelete { get; set; }

        public IReadOnlyList<string> Deleted => _deleted;

        public Task<DeleteSessionResponse> DeleteSessionAsync(string sessionId, CancellationToken ct = default) {
            OnDelete?.Invoke();
            _deleted.Add(sessionId);

            return Failure is { } failure ? Task.FromException<DeleteSessionResponse>(failure) : Task.FromResult(Answer);
        }

        public Task HideSessionAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<SetTitleResult> SetSessionTitleAsync(string sessionId, string title, bool rename, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ErrorsResult> GetErrorsAsync(string sessionId, bool chain, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RecapResult> GetRecapAsync(string sessionId, bool chain, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TurnsResult> GetTurnsAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TurnDetailResult> GetTurnAsync(string sessionId, int turnIndex, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PlanArtifactsResult> GetPlanArtifactsAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    sealed class FakeDaemon : IDaemonRecordingClient {
        readonly List<(string Daemon, string Session)> _asked = [];

        public RecordingDisableAnswer Answer { get; set; } = new(RecordingDisable.NoSocket);

        /// <summary>What a daemon other than the default answers with.</summary>
        public Dictionary<string, RecordingDisableAnswer> Answers { get; } = [];

        /// <summary>The daemons this machine knows of; null when that can't be read.</summary>
        public IReadOnlyList<string>? Names { get; set; } = [];

        public IReadOnlyList<string>? Daemons() => Names;

        public bool Hangs { get; set; }

        public Action? OnAsked { get; set; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(string Daemon, string Session)> Asked {
            get {
                lock (_asked) return [.._asked];
            }
        }

        public Task<RecordingHandOver> TryRecordAsync(RecordingSource source, bool skipTitle, int? follows, CancellationToken ct) => Task.FromResult(RecordingHandOver.Unanswered);

        public Task<RecordingFinish> FinishSourceAsync(string key, string? holder, ProcessIncarnation? from, CancellationToken ct) => Task.FromResult(RecordingFinish.NotHeld);

        public async Task<RecordingDisableAnswer> DisableSessionAsync(string daemonName, string sessionId, CancellationToken ct) {
            lock (_asked) _asked.Add((daemonName, sessionId));
            OnAsked?.Invoke();
            Reached.TrySetResult();

            if (Hangs) await new TaskCompletionSource().Task;

            return Answers.GetValueOrDefault(daemonName, Answer);
        }
    }
}
