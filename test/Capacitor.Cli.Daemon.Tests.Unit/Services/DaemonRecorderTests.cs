using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>What the daemon owes a source across its life: a claim before a recorder, a list entry a hard kill can be
/// picked up from, and both let go of only when the source is really over.</summary>
public class DaemonRecorderTests {
    const string Url     = "http://127.0.0.1:1";
    const string Name    = "daemon-a";
    const string Session = "0123456789abcdef0123456789abcdef";
    const string Prompt  = """{"type":"user","message":{"content":"Summarise the README"}}""";
    const string Reply   = """{"type":"assistant","message":{"content":[{"type":"text","text":"Done."}]}}""";

    static readonly Func<Task<bool>> InScope = () => Task.FromResult(false);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    FakeRecorderTransport Transport { get; } = new() { IsConnected = true };

    int Connections { get; set; }

    List<RecordedSource> Housekept { get; } = [];

    Action? HousekeepingBeat { get; set; }

    DaemonConfig NewConfig(bool on = true) => new() {
        Name = Name, ServerUrl = Url, ConfigRoot = Config.Root, Store = Daemons.Store, Profiles = Resolutions.None(Config.Root), RecordHostedClaude = on,
    };

    /// <param name="prepare">What a recorder's preparing waits on, which ignores its token as repository detection does.</param>
    /// <param name="probe">The server's answer, which is yes unless the test holds it.</param>
    DaemonRecorder NewRecorder(bool on = true, TimeProvider? time = null, Func<Task>? prepare = null, RecordingProbe? probe = null) {
        if (probe is null) {
            probe = new RecordingProbe();
            probe.Report(true);
        }

        return new DaemonRecorder(
            NewConfig(on), new CapacitorServer(Url, Config.Root, Resolutions.None(Config.Root)), probe,
            () => {
                Connections++;

                return Transport;
            },
            (source, _, _, beat) => {
                lock (Housekept) Housekept.Add(source);
                HousekeepingBeat = beat;

                return new FakeRecorderHousekeeping { OnPrepare = prepare };
            },
            time ?? TimeProvider.System, NullLogger<DaemonRecorder>.Instance);
    }

    WatcherPaths Paths => WatcherPaths.FromEnvironment(Config.Root);

    RecorderClaims Claims => field ??= new(Paths.Directory, TimeProvider.System);

    RecordedSourceList List => new(Daemons.Store.StateDirectory(Name), NullLogger.Instance);

    RecordedSource Source(string? transcript = null, string? subagent = null) =>
        new("agent-1", Session, subagent, transcript ?? Tmp.PathTo("t.jsonl"), "claude", null, false);

    string Transcript(params string[] lines) => Tmp.CreateFile("t.jsonl", string.Concat(lines.Select(line => $"{line}\n")));

    /// <summary>A watcher's pid file naming this very process, which is as alive as a pid gets.</summary>
    async Task LiveWatcher(string key) {
        Directory.CreateDirectory(Paths.Directory);
        await File.WriteAllTextAsync(Paths.PidFile(key), $"{Environment.ProcessId}\n{PidIdentity.Capture(Environment.ProcessId)}");
    }

    /// <summary>With no claim, a host's recorder reads its source as lost and silently records nothing, so the source
    /// must not reach it. Here the claim is another daemon's, still beating.</summary>
    [Test]
    public async Task A_source_added_without_its_claim_is_refused() {
        await using var recorder = NewRecorder();
        var source = Source();
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, "another-daemon", isHosted: true);

        await Assert.That(await recorder.TryRecordAsync(source, CancellationToken.None)).IsFalse();

        await Assert.That(recorder.ActiveKeys).IsEmpty();
        await Assert.That(Connections).IsEqualTo(0);
        await Assert.That(List.Load()).IsEmpty();
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    [Test]
    public async Task A_source_a_live_watcher_holds_is_refused() {
        await using var recorder = NewRecorder();
        var source = Source();
        await LiveWatcher(source.Key);

        await Assert.That(await recorder.TryRecordAsync(source, CancellationToken.None)).IsFalse();

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(recorder.ActiveKeys).IsEmpty();
    }

    /// <summary>A hook holding the spawn lock is seconds of file work, not an answer about who records the source.</summary>
    [Test]
    public async Task A_lost_spawn_lock_is_retried_rather_than_read_as_a_refusal() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Source();
        Directory.CreateDirectory(Paths.Directory);
        var lockFile = new FileStream(Paths.SpawnLockFile(source.Key), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

        var claim = recorder.ClaimAsync(source, CancellationToken.None);
        await Assert.That(claim.IsCompleted).IsFalse();
        await lockFile.DisposeAsync();

        await WaitHarness.PollUntilAsync(() => {
            time.Advance(TimeSpan.FromMilliseconds(50));

            return claim.IsCompleted;
        });

        await Assert.That(await claim).IsTrue();
        await Assert.That(Claims.Read(source.Key)).IsNotEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task A_finished_source_releases_its_claim_and_a_lost_one_does_not() {
        await using var recorder = NewRecorder();
        var finished = Source(Transcript(Prompt, Reply));
        var lost     = Source(Tmp.PathTo("lost.jsonl")) with { SessionId = "fedcba9876543210fedcba9876543210" };

        await recorder.TryRecordAsync(finished, CancellationToken.None);
        await Assert.That(await recorder.FinishAsync(finished.Key, CancellationToken.None).WithinDeadlineAsync("the finish")).IsTrue();

        await Assert.That(Claims.Read(finished.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(recorder.ActiveKeys).IsEmpty();

        // Whoever took the source claimed it after this recorder's: the claim file is theirs now, and only the source's
        // own end says so.
        await recorder.ClaimAsync(lost, CancellationToken.None);
        Claims.Release(lost.Key);
        Claims.TryTake(lost.Key, "another-daemon", isHosted: true);

        recorder.OnAttemptEnded(new RecorderAttemptEnded(lost.Recording, SourceEnded: true, LostClaim: true, EndedIdle: false, [], null));
        await WaitHarness.PollUntilAsync(() => List.Load().Count == 0);

        await Assert.That(Claims.Read(lost.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    /// <summary>A source that ends by itself keeps nothing either. A claim left behind would read as stale hosted, and the
    /// next hook, which spawns nothing for that, would leave later rows with no recorder.</summary>
    [Test]
    public async Task A_source_that_ended_by_itself_lets_its_claim_go() {
        await using var recorder = NewRecorder();
        var source = Source(Transcript(Prompt, Reply), "a1");
        await recorder.TryRecordAsync(source, CancellationToken.None);

        recorder.OnAttemptEnded(new RecorderAttemptEnded(source.Recording, SourceEnded: true, LostClaim: false, EndedIdle: true, [], null));
        await WaitHarness.PollUntilAsync(() => Claims.Read(source.Key) is RecorderClaimState.None && List.Load().Count == 0);

        await Assert.That(recorder.ActiveKeys).IsEmpty();
    }

    /// <summary>A retry is the same source carrying on, so a failed attempt decides nothing.</summary>
    [Test]
    public async Task A_failed_attempt_keeps_its_claim() {
        await using var recorder = NewRecorder();
        var source = Source(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);

        recorder.OnAttemptEnded(new RecorderAttemptEnded(source.Recording, SourceEnded: false, LostClaim: false, EndedIdle: false, [], new InvalidOperationException()));

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(recorder.ActiveKeys).Contains(source.Key);
    }

    /// <summary>A restart would otherwise take a source back from the hook that finished it.</summary>
    [Test]
    public async Task A_lost_source_leaves_the_persisted_list() {
        await using var recorder = NewRecorder();
        var source = Source(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await Assert.That(List.Load().Select(s => s.Key)).IsEquivalentTo([source.Key]);

        Claims.Release(source.Key);

        await WaitHarness.PollUntilAsync(() => recorder.ActiveKeys.Count == 0 && List.Load().Count == 0);
        await Assert.That(Transport.Detached).IsEquivalentTo([source.Key]);
    }

    /// <summary>What a killed daemon leaves is the list and a claim nobody beats. Its successor starts where the server
    /// says, so a line is neither lost nor sent twice.</summary>
    [Test]
    public async Task A_hard_killed_daemon_resumes_from_WatcherConnect() {
        var source = Source(Transcript(Prompt, Reply, Prompt, Reply, Prompt));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);
        Transport.ResumeFrom = 2;

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(InScope, CancellationToken.None);

        await WaitHarness.PollUntilAsync(() => Transport.Batches.SelectMany(batch => batch.LineNumbers ?? []).Count() >= 3);

        await Assert.That(Transport.Batches.SelectMany(batch => batch.LineNumbers ?? [])).IsEquivalentTo([2, 3, 4]);
        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    [Test]
    public async Task A_restart_skips_a_session_a_live_watcher_holds() {
        var source = Source(Transcript(Prompt, Reply));
        List.TryAdd(source);
        await LiveWatcher(source.Key);

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(InScope, CancellationToken.None);

        await Assert.That(recorder.ActiveKeys).IsEmpty();
        await Assert.That(List.Load()).IsEmpty();
        await Assert.That(Connections).IsEqualTo(0);
    }

    /// <summary>A claim beside that watcher would keep every hook from ever stopping it.</summary>
    [Test]
    public async Task A_restart_that_skips_a_source_lets_its_old_claim_go() {
        var source = Source(Transcript(Prompt));
        List.TryAdd(source);
        await LiveWatcher(source.Key);
        Claims.TryTake(source.Key, Name, isHosted: true);

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(InScope, CancellationToken.None);

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>Nothing else would end it: its agent is gone, and no exit is coming.</summary>
    [Test]
    public async Task A_restored_source_whose_agent_did_not_come_back_finishes() {
        var source = Source(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(InScope, CancellationToken.None);
        recorder.SettleRestored(_ => false);

        await WaitHarness.PollUntilAsync(() => recorder.ActiveKeys.Count == 0 && Claims.Read(source.Key) is RecorderClaimState.None && List.Load().Count == 0);
    }

    [Test]
    public async Task A_restored_source_whose_agent_came_back_stays_and_is_that_agents() {
        var source = Source(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(InScope, CancellationToken.None);

        await Assert.That(await recorder.ClaimAsync(source with { AgentId = "agent-2" }, CancellationToken.None)).IsTrue();
        recorder.StartForAgent(source with { AgentId = "agent-2" });
        recorder.SettleRestored(_ => false);

        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
        await recorder.FinishAgentAsync("agent-2", TimeSpan.FromSeconds(30)).WithinDeadlineAsync("the agent's finish");
        await Assert.That(recorder.ActiveKeys).IsEmpty();
    }

    /// <summary>Recording was turned off with sources left. A hosted claim nobody comes back for would keep a resumed
    /// session's hooks from starting the watcher it now needs.</summary>
    [Test]
    public async Task A_daemon_with_recording_off_lets_go_of_what_it_finds() {
        var source = Source(Transcript(Prompt));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);

        await using var recorder = NewRecorder(on: false);
        await recorder.RestoreAsync(InScope, CancellationToken.None);

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load()).IsEmpty();
        await Assert.That(Connections).IsEqualTo(0);
    }

    /// <summary>A profile with an exclusion leaves every session to a hook's gate, and a source picked up here would
    /// skip it. An in-scope session comes back when its next hook hands it over.</summary>
    [Test]
    public async Task A_restart_under_a_profile_that_scopes_capture_records_nothing() {
        var source = Source(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(() => Task.FromResult(true), CancellationToken.None);

        await Assert.That(recorder.ActiveKeys).IsEmpty();
        await Assert.That(Connections).IsEqualTo(0);
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load()).IsEmpty();
    }

    [Test]
    public async Task A_restart_under_a_profile_that_scopes_capture_keeps_another_daemons_claim() {
        var source = Source(Transcript(Prompt));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, "another-daemon", isHosted: true);

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(() => Task.FromResult(true), CancellationToken.None);

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    /// <summary>A flag turned off lets go of its own claims. One beside it is another daemon's, and may be recording.</summary>
    [Test]
    public async Task A_daemon_with_recording_off_keeps_another_daemons_claim() {
        var source = Source(Transcript(Prompt));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, "another-daemon", isHosted: true);

        await using var recorder = NewRecorder(on: false);
        await recorder.RestoreAsync(InScope, CancellationToken.None);

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>A local spawn can beat the boot's restore while the server is down, and its write must not drop what
    /// the last daemon left.</summary>
    [Test]
    public async Task A_source_taken_ahead_of_the_restore_keeps_what_the_last_daemon_left() {
        var left = Source(Transcript(Prompt));
        List.TryAdd(left);
        var early = Source(Tmp.PathTo("early.jsonl")) with { SessionId = "fedcba9876543210fedcba9876543210" };

        await using var recorder = NewRecorder();
        await recorder.ClaimAsync(early, CancellationToken.None);

        await Assert.That(List.Load().Select(source => source.Key)).IsEquivalentTo([left.Key, early.Key]);
    }

    /// <summary>The entry is the running source's own: picking it up again would be refused and lose that source's claim.</summary>
    [Test]
    public async Task A_restore_passes_over_a_key_already_running() {
        var source = Source(Transcript(Prompt, Reply));
        await using var recorder = NewRecorder();
        await recorder.TryRecordAsync(source, CancellationToken.None);

        await recorder.RestoreAsync(InScope, CancellationToken.None);

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
        await Assert.That(List.Load().Select(left => left.Key)).IsEquivalentTo([source.Key]);
    }

    [Test]
    public async Task A_restart_under_a_scoped_profile_keeps_a_source_a_hook_already_handed_over() {
        var source = Source(Transcript(Prompt, Reply));
        await using var recorder = NewRecorder();
        await recorder.TryRecordAsync(source, CancellationToken.None);

        await recorder.RestoreAsync(() => Task.FromResult(true), CancellationToken.None);

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    /// <summary>A hand-edited or half-understood list is not a reason to lose what else it holds.</summary>
    [Test]
    public async Task A_list_with_holes_keeps_its_good_entries() {
        var source = Source(Transcript(Prompt));
        List.TryAdd(source);
        var path = Path.Combine(Daemons.Store.StateDirectory(Name), "recording-sources.json");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("[", """[null,{"AgentId":"a"},"""));

        await Assert.That(List.Load().Select(left => left.Key)).IsEquivalentTo([source.Key]);
    }

    /// <summary>Another daemon's fresh claim outlasts the restore's window, and a launch of the same session waits on
    /// neither: it goes on unclaimed and its hook decides.</summary>
    [Test]
    public async Task A_claim_behind_a_slow_restore_gives_up_and_lets_the_launch_go() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(Transcript(Prompt));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, "another-daemon", isHosted: true);

        await using var recorder = NewRecorder(time: time);
        using var cts = new CancellationTokenSource();
        var restore = recorder.RestoreAsync(InScope, cts.Token);

        var claim = recorder.ClaimAsync(source with { AgentId = "agent-2" }, CancellationToken.None);
        await Assert.That(claim.IsCompleted).IsFalse();

        await WaitHarness.PollUntilAsync(() => {
            time.Advance(TimeSpan.FromMilliseconds(100));

            return claim.IsCompleted;
        });

        await Assert.That(await claim).IsFalse();
        await cts.CancelAsync();
        await Assert.That(async () => await restore).Throws<OperationCanceledException>();
    }

    /// <summary>The session resumed while its last drain was going out. Told it is held, the launch would have no
    /// recorder once that drain ends and no claim to show for it; told it isn't, its next hook hands it over.</summary>
    [Test]
    public async Task A_session_resumed_behind_its_own_drain_is_not_adopted() {
        await using var recorder = NewRecorder();
        var source  = Source(Transcript(Prompt, Reply));
        var resumed = source with { AgentId = "agent-2" };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Transport.OnDrainComplete = _ => release.Task;
        await recorder.TryRecordAsync(source, CancellationToken.None);

        var finishing = recorder.FinishAgentAsync("agent-1", TimeSpan.FromSeconds(30));

        await Assert.That(await recorder.ClaimAsync(resumed, CancellationToken.None).WithinDeadlineAsync("the claim")).IsFalse();
        recorder.StartForAgent(resumed);

        release.TrySetResult();
        await finishing.WithinDeadlineAsync("the finish");

        await Assert.That(recorder.ActiveKeys).IsEmpty();
        await Assert.That(await recorder.TryRecordAsync(resumed, CancellationToken.None).WithinDeadlineAsync("the hand-over")).IsTrue();
        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
        await Assert.That(List.Load().Single().AgentId).IsEqualTo("agent-2");
    }

    /// <summary>A hook arriving between an exit and the end of that exit's drain would be told the source is held, and
    /// have nothing recording it a moment later.</summary>
    [Test]
    public async Task A_hand_over_behind_a_finishing_agent_waits_for_the_source_to_go() {
        await using var recorder = NewRecorder();
        var source  = Source(Transcript(Prompt, Reply));
        var resumed = source with { AgentId = "agent-2" };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Transport.OnDrainComplete = _ => release.Task;
        await recorder.TryRecordAsync(source, CancellationToken.None);

        var finishing = recorder.FinishAgentAsync("agent-1", TimeSpan.FromSeconds(30));
        var handed    = recorder.TryRecordAsync(resumed, CancellationToken.None);
        await Assert.That(handed.IsCompleted).IsFalse();

        release.TrySetResult();
        await finishing.WithinDeadlineAsync("the finish");

        await Assert.That(await handed.WithinDeadlineAsync("the hand-over")).IsTrue();
        await Assert.That(List.Load().Single().AgentId).IsEqualTo("agent-2");
    }

    [Test]
    public async Task A_key_nothing_records_is_not_finished() {
        await using var recorder = NewRecorder();

        await Assert.That(await recorder.FinishAsync(Session, CancellationToken.None)).IsFalse();
        await Assert.That(Connections).IsEqualTo(0);
    }

    /// <summary>A hook that stops waiting has still asked, so the drain goes on without it.</summary>
    [Test]
    public async Task A_finish_given_up_on_still_finishes() {
        await using var recorder = NewRecorder();
        var source  = Source(Transcript(Prompt, Reply));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Connects == 1);
        Transport.OnDrainComplete = _ => release.Task;

        using var giveUp = new CancellationTokenSource();
        var finish = recorder.FinishAsync(source.Key, giveUp.Token, hurried: true);
        await WaitHarness.PollUntilAsync(() => Transport.DrainCompletes == 1);
        await giveUp.CancelAsync();

        await Assert.That(async () => await finish).Throws<OperationCanceledException>();
        await Assert.That(recorder.ActiveKeys).Contains(source.Key);

        release.SetResult();
        await WaitHarness.PollUntilAsync(() => Claims.Read(source.Key) is RecorderClaimState.None);
    }

    /// <summary>An exited agent's sources all go, a subagent's included, and another agent's stay.</summary>
    [Test]
    public async Task An_exited_agent_finishes_only_its_own_sources() {
        await using var recorder = NewRecorder();
        var root  = Source(Transcript(Prompt, Reply));
        var child = Source(Tmp.PathTo("child.jsonl"), "a1");
        var other = Source(Tmp.PathTo("other.jsonl")) with { AgentId = "agent-2", SessionId = "fedcba9876543210fedcba9876543210" };

        foreach (var source in new[] { root, child, other }) await recorder.TryRecordAsync(source, CancellationToken.None);

        await recorder.FinishAgentAsync("agent-1", TimeSpan.FromSeconds(30)).WithinDeadlineAsync("the agent's finish");

        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([other.Key]);
    }

    /// <summary>A hung send can't hold up an agent's end: the budget ends the wait, and the drain is left running.</summary>
    [Test]
    public async Task An_exited_agents_finish_is_bounded() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Source(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Connects == 1);
        Transport.OnDrainComplete = _ => new TaskCompletionSource().Task;

        var finish = recorder.FinishAgentAsync("agent-1", TimeSpan.FromSeconds(10));
        await WaitHarness.PollUntilAsync(() => Transport.DrainCompletes == 1);
        time.Advance(TimeSpan.FromSeconds(10));

        await finish.WithinDeadlineAsync("the bound");
        await Assert.That(recorder.ActiveKeys).Contains(source.Key);

        // The teardown asks again on its way through, and a drain already asked for costs it nothing.
        await recorder.FinishAgentAsync("agent-1", TimeSpan.FromSeconds(10)).WithinDeadlineAsync("the second ask");
    }

    [Test]
    public async Task A_shutdown_keeps_what_comes_back_and_lets_go_of_what_does_not() {
        await using var recorder = NewRecorder();
        var resumes = Source(Transcript(Prompt, Reply));
        var ends    = Source(Tmp.PathTo("ends.jsonl")) with { AgentId = "agent-2", SessionId = "fedcba9876543210fedcba9876543210" };

        await recorder.TryRecordAsync(resumes, CancellationToken.None);
        await recorder.TryRecordAsync(ends, CancellationToken.None);

        await recorder.StopAsync(agentId => agentId == "agent-1", CancellationToken.None).WithinDeadlineAsync("the stop");

        await Assert.That(Claims.Read(resumes.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(Claims.Read(ends.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load().Select(s => s.Key)).IsEquivalentTo([resumes.Key]);
    }

    /// <summary>A source stuck where no token reaches hears no callback, and its recorder may yet send: the next daemon
    /// finishes it, so its claim and entry stay, whatever its agent does.</summary>
    [Test]
    public async Task A_source_stuck_at_shutdown_keeps_its_claim_and_entry() {
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var recorder = NewRecorder(prepare: () => stuck.Task);
        var source = Source(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);

        await recorder.StopAsync(_ => false, new CancellationToken(canceled: true)).WithinDeadlineAsync("the stop");

        await Assert.That(recorder.ActiveKeys).Contains(source.Key);
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(List.Load().Select(s => s.Key)).IsEquivalentTo([source.Key]);

        stuck.SetResult();
    }

    /// <summary>A host that is stopping refuses the add, and nothing would beat a claim left behind.</summary>
    [Test]
    public async Task A_source_refused_by_a_stopping_host_lets_its_claim_go() {
        await using var recorder = NewRecorder();
        var first = Source(Transcript(Prompt, Reply));
        var late  = Source(Tmp.PathTo("late.jsonl"), "a1");
        await recorder.TryRecordAsync(first, CancellationToken.None);
        await recorder.StopAsync(_ => true, CancellationToken.None).WithinDeadlineAsync("the stop");

        await Assert.That(await recorder.TryRecordAsync(late, CancellationToken.None)).IsFalse();

        await Assert.That(Claims.Read(late.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load().Select(s => s.Key)).IsEquivalentTo([first.Key]);
    }

    /// <summary>The path is computed ahead of the launch, and Claude's own spelling of a project directory can differ.
    /// Left alone, the recorder would tail a file that never comes and the session would go unrecorded.</summary>
    [Test]
    public async Task A_transcript_found_elsewhere_moves_its_source() {
        await using var recorder = NewRecorder();
        var source = Source(Tmp.PathTo("computed.jsonl"));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Connects == 1);

        var found = source with { TranscriptPath = Transcript(Prompt, Reply) };
        await recorder.ObserveTranscriptAsync(found).WithinDeadlineAsync("the move");

        await Assert.That(List.Load().Single().TranscriptPath).IsEqualTo(found.TranscriptPath);
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    /// <summary>A path that exists is the one to keep: an owned worktree's goes through a link discovery resolves.</summary>
    [Test]
    public async Task A_transcript_found_where_it_was_expected_moves_nothing() {
        await using var recorder = NewRecorder();
        var source = Source(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);

        await recorder.ObserveTranscriptAsync(source with { TranscriptPath = Tmp.PathTo("resolved.jsonl") }).WithinDeadlineAsync("the look");

        await Assert.That(List.Load().Single().TranscriptPath).IsEqualTo(source.TranscriptPath);
    }

    [Test]
    public async Task One_connection_serves_every_source_and_starts_with_the_first() {
        await using var recorder = NewRecorder();
        await Assert.That(Connections).IsEqualTo(0);

        await recorder.TryRecordAsync(Source(Transcript(Prompt, Reply)), CancellationToken.None);
        await recorder.TryRecordAsync(Source(Tmp.PathTo("child.jsonl"), "a1"), CancellationToken.None);

        await Assert.That(Connections).IsEqualTo(1);
    }

    [Test]
    public async Task No_recording_on_a_server_without_it() {
        var probe = new RecordingProbe();
        probe.Report(false);
        await using var recorder = NewRecorder(probe: probe);

        await Assert.That(recorder.Enabled).IsFalse();
    }

    /// <summary>A hook takes a source to a daemon only while this holds, and an answer that comes later opens it.</summary>
    [Test]
    public async Task No_recording_until_the_server_has_answered() {
        var probe = new RecordingProbe();
        await using var recorder = NewRecorder(probe: probe);
        await Assert.That(recorder.Enabled).IsFalse();

        probe.Report(true);

        await Assert.That(recorder.Enabled).IsTrue();
    }

    /// <summary>The boot's resumes wait behind a restore, which waits for the answer it needs and no longer than this.</summary>
    [Test]
    public async Task A_restore_waits_for_the_servers_answer() {
        var source = Source(Transcript(Prompt, Reply));
        List.TryAdd(source);
        var probe = new RecordingProbe();
        await using var recorder = NewRecorder(probe: probe);

        var restore = recorder.RestoreAsync(InScope, CancellationToken.None);
        await Assert.That(restore.IsCompleted).IsFalse();

        probe.Report(true);
        await restore.WithinDeadlineAsync("the restore");

        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    /// <summary>No answer decides nothing, so nothing is let go of: the next boot has the list, and a hook the claim.</summary>
    [Test]
    public async Task A_restore_with_no_answer_leaves_what_it_found() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);
        await using var recorder = NewRecorder(time: time, probe: new RecordingProbe());

        var restore = recorder.RestoreAsync(InScope, CancellationToken.None);
        await Assert.That(restore.IsCompleted).IsFalse();

        time.Advance(DaemonRecorder.AnswerWait);
        await restore.WithinDeadlineAsync("the restore");

        await Assert.That(recorder.ActiveKeys).IsEmpty();
        await Assert.That(Connections).IsEqualTo(0);
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(List.Load().Select(left => left.Key)).IsEquivalentTo([source.Key]);
    }

    [Test]
    public async Task A_restore_on_a_server_without_recording_lets_go_of_what_it_found() {
        var source = Source(Transcript(Prompt));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);
        var probe = new RecordingProbe();
        probe.Report(false);
        await using var recorder = NewRecorder(probe: probe);

        await recorder.RestoreAsync(InScope, CancellationToken.None).WithinDeadlineAsync("the restore");

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>A flag that is off has no use for the answer, which never comes.</summary>
    [Test]
    public async Task A_restore_with_the_flag_off_waits_for_nothing() {
        var source = Source(Transcript(Prompt));
        List.TryAdd(source);
        await using var recorder = NewRecorder(on: false, probe: new RecordingProbe());

        await recorder.RestoreAsync(InScope, CancellationToken.None).WithinDeadlineAsync("the restore");

        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>A stop with nothing recording leaves no host to refuse the next source, and that source's connection would
    /// open with the daemon on its way out.</summary>
    [Test]
    public async Task A_stop_before_any_source_refuses_the_first_one() {
        await using var recorder = NewRecorder();

        await recorder.StopAsync(_ => false, CancellationToken.None);

        var source = Source(Transcript(Prompt));
        await Assert.That(await recorder.TryRecordAsync(source, CancellationToken.None)).IsFalse();

        await Assert.That(Connections).IsEqualTo(0);
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>Housekeeping can run long between a recorder's beats, and a claim going stale lets a hook take a source
    /// that is recording.</summary>
    [Test]
    public async Task A_housekeeping_beat_keeps_its_sources_claim_fresh() {
        var time    = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims  = new RecorderClaims(Paths.Directory, time);
        await using var recorder = NewRecorder(time: time, prepare: () => {
            preparing.TrySetResult();

            return release.Task;
        });
        var source = Source(Transcript(Prompt));

        await recorder.TryRecordAsync(source, CancellationToken.None);
        await preparing.Task.WithinDeadlineAsync("the recorder to reach its housekeeping");
        time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));
        await Assert.That(claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonStale);

        HousekeepingBeat!();

        await Assert.That(claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        release.SetResult();
    }

    [Test]
    public async Task With_the_flag_off_the_recorder_is_not_enabled() {
        await using var recorder = NewRecorder(on: false);

        await Assert.That(recorder.Enabled).IsFalse();
    }
}
