using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
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
    const string HeldLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"key ghp_0123456789abcdefghijklmnopqrstuvwxyz"}]}}""";

    static readonly Func<Task<RecordingScope>> InScope = () => Task.FromResult(new RecordingScope(false, _ => true));

    static Func<Task<RecordingScope>> LeftToHooks(bool mayImport = true) => () => Task.FromResult(new RecordingScope(true, _ => mayImport));

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
    DaemonRecorder NewRecorder(bool on = true, TimeProvider? time = null, Func<Task>? prepare = null, RecordingProbe? probe = null, string name = Name) {
        if (probe is null) {
            probe = new RecordingProbe();
            probe.Report(true);
        }

        var config = NewConfig(on);
        config.Name = name;

        return new DaemonRecorder(
            config, new CapacitorServer(Url, Config.Root, Resolutions.None(Config.Root)), probe,
            () => {
                Connections++;

                return Transport;
            },
            (source, _, _, beat) => {
                lock (Housekept) Housekept.Add(source);
                HousekeepingBeat = beat;

                return new FakeRecorderHousekeeping { OnPrepare = prepare };
            },
            time ?? TimeProvider.System, NullLogger<DaemonRecorder>.Instance, process => {
                Interlocked.Increment(ref _processLooks);

                return Exited.Contains(process.Pid);
            },
            (source, end) => {
                lock (Ends) Ends.Add((source.Key, end, (time ?? TimeProvider.System).GetUtcNow()));

                return Task.FromResult(true);
            });
    }

    /// <summary>Every session end the recorder posted, with the time it posted it.</summary>
    List<(string Key, System.Text.Json.Nodes.JsonObject End, DateTimeOffset At)> Ends { get; } = [];

    int EndCount {
        get {
            lock (Ends) return Ends.Count;
        }
    }

    /// <summary>What a hook leaves when a process claims a session.</summary>
    void ClaimNote(int pid, string session = Session, string token = "test:claude") {
        Directory.CreateDirectory(Config.Root.Path("agent-sessions"));
        File.WriteAllText(Config.Root.Path("agent-sessions", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)), $"{session}\n{token}");
    }

    /// <summary>Moves the clock a second at a time until <paramref name="condition"/> holds.</summary>
    static async Task Stepping(FakeTimeProvider time, Func<bool> condition) =>
        await WaitHarness.PollUntilAsync(() => {
            if (condition()) return true;

            time.Advance(TimeSpan.FromSeconds(1));

            return condition();
        });

    /// <summary>The pids of terminal processes that have gone.</summary>
    ConcurrentBag<int> Exited { get; } = [];

    int _processLooks;

    /// <summary>Moves the clock one process check at a time until the daemon has looked twice more, so the first of those
    /// rounds has finished whatever it was going to.</summary>
    async Task TwoProcessChecks(FakeTimeProvider time) {
        var looked = Volatile.Read(ref _processLooks);

        await WaitHarness.PollUntilAsync(() => {
            time.Advance(DaemonRecorder.ProcessCheck);

            return Volatile.Read(ref _processLooks) >= looked + 2;
        });
    }

    WatcherPaths Paths => WatcherPaths.FromEnvironment(Config.Root);

    RecorderClaims Claims => field ??= new(Paths.Directory, TimeProvider.System);

    RecordedSourceList List => new(Daemons.Store.StateDirectory(Name), NullLogger.Instance);

    RecordedSource Source(string? transcript = null, string? subagent = null) =>
        new("agent-1", Session, subagent, transcript ?? Tmp.PathTo("t.jsonl"), "claude", null, false);

    /// <summary>A terminal session's source, which its Claude process ends rather than an agent.</summary>
    static RecordedSource Terminal(string transcript, string session = Session, int pid = 4242) =>
        new(null, session, null, transcript, "claude", null, false, Process: new ProcessIncarnation(pid, "test:claude"));

    string Transcript(params string[] lines) => Tmp.CreateFile("t.jsonl", string.Concat(lines.Select(line => $"{line}\n")));

    /// <summary>A watcher's pid file naming this very process, which is as alive as a pid gets.</summary>
    async Task LiveWatcher(string key) {
        Directory.CreateDirectory(Paths.Directory);
        await File.WriteAllTextAsync(Paths.PidFile(key), $"{Environment.ProcessId}\n{PidIdentity.Capture(Environment.ProcessId)}");
    }

    /// <summary>A hook outside any agent left a terminal claim, which a hook from a hosted agent never waits on, and no
    /// agent's exit ends the source.</summary>
    [Test]
    public async Task A_terminal_source_takes_a_terminal_claim_and_no_agents_exit_ends_it() {
        await using var recorder = NewRecorder();
        var source = Terminal(Transcript(Prompt, Reply));

        await Assert.That(await recorder.TryRecordAsync(source, CancellationToken.None)).IsTrue();
        await recorder.FinishAgentAsync("agent-1", TimeSpan.FromSeconds(5)).WithinDeadlineAsync("an agent's end");

        await Assert.That(Claims.IsHosted(source.Key)).IsFalse();
        await Assert.That(Claims.Holder(source.Key)).IsEqualTo(Name);
        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    /// <summary>A terminal session has no agent exit to end it. Once its Claude process has gone the daemon finishes the
    /// source by itself, on its own round of looking, with no hook involved.</summary>
    [Test]
    public async Task A_terminal_source_ends_once_its_process_has_gone() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        await TwoProcessChecks(time);
        await Assert.That(recorder.Holds(source.Key)).IsTrue();

        Exited.Add(4242);
        await WaitHarness.PollUntilAsync(() => {
            time.Advance(TimeSpan.FromSeconds(1));

            return recorder.ActiveKeys.Count == 0 && Claims.Read(source.Key) is RecorderClaimState.None && List.Load().Count == 0;
        });

        await Assert.That(Transport.DrainCompletes).IsEqualTo(1);
    }

    /// <summary>A terminal session killed with no SessionEnd hook gets the end a watcher's parent monitor would post, once
    /// the grace for a late hook has passed. Until then the claim and the list entry stay, so a hook in the grace finds
    /// the source, and the daemon claims that process's end first, so a late hook posts none of its own.</summary>
    [Test]
    public async Task A_terminal_session_whose_process_exits_unannounced_gets_its_end_after_the_grace() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var claims = new RecorderClaims(Paths.Directory, time);
        await using var recorder = NewRecorder(time: time);
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        Exited.Add(4242);
        recorder.FinishExitedProcesses();
        await WaitHarness.PollUntilAsync(() => Transport.DrainCompletes == 1);
        var drained = time.GetUtcNow();

        await Assert.That(EndCount).IsEqualTo(0);
        await Assert.That(claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(List.Load().Select(s => s.Key)).IsEquivalentTo([source.Key]);

        await Stepping(time, () => EndCount == 1 && claims.Read(source.Key) is RecorderClaimState.None && List.Load().Count == 0);

        var (_, end, at) = Ends.Single();
        await Assert.That(at - drained).IsGreaterThanOrEqualTo(DaemonRecorder.SessionEndGrace);
        await Assert.That(end["reason"]?.GetValue<string>()).IsEqualTo("parent_exited");
        await Assert.That(end["session_id"]?.GetValue<string>()).IsEqualTo(Session);
        await Assert.That(end.ContainsKey("agent_host_id")).IsFalse();
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, source.Process!, time.GetUtcNow())).IsEqualTo(SessionEndTurn.Done);
    }

    /// <summary>The SessionEnd hook's continuation runs detached and can land after the process has gone. Its finish
    /// inside the grace is answered once the exit's drain is done, and the end it claims for that process holds the
    /// daemon's back.</summary>
    [Test]
    public async Task A_session_end_hook_inside_the_grace_keeps_the_daemon_from_posting_an_end() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        Exited.Add(4242);
        recorder.FinishExitedProcesses();
        await WaitHarness.PollUntilAsync(() => Transport.DrainCompletes == 1);

        await Assert.That(await recorder.FinishForHookAsync(source.Key, source.Process, CancellationToken.None).WithinDeadlineAsync("the hook's finish")).IsTrue();
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, source.Process!, time.GetUtcNow())).IsEqualTo(SessionEndTurn.Yours);
        SessionEndRecord.MarkPosted(Paths, Session, source.Process!, time.GetUtcNow());
        await Stepping(time, () => List.Load().Count == 0);

        await Assert.That(EndCount).IsEqualTo(0);
        await Assert.That(Transport.DrainCompletes).IsEqualTo(1);
    }

    /// <summary>The hook got there first, so the exit that follows decides nothing: one end, the hook's.</summary>
    [Test]
    public async Task A_hook_finish_ahead_of_the_process_check_leaves_no_end_to_post() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        var hook = recorder.FinishForHookAsync(source.Key, source.Process, CancellationToken.None);
        Exited.Add(4242);
        recorder.FinishExitedProcesses();
        await Stepping(time, () => hook.IsCompleted);
        await hook;
        time.Advance(DaemonRecorder.SessionEndGrace * 2);
        await Task.Delay(50);

        await Assert.That(EndCount).IsEqualTo(0);
        await Assert.That(Transport.DrainCompletes).IsEqualTo(1);
    }

    /// <summary>A subagent's source ends with its process, and only the session posts an end.</summary>
    [Test]
    public async Task A_terminal_subagent_whose_process_exits_posts_no_end() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Terminal(Transcript(Prompt, Reply)) with { SubagentId = "a1" };
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        Exited.Add(4242);
        recorder.FinishExitedProcesses();
        await Stepping(time, () => List.Load().Count == 0);
        time.Advance(DaemonRecorder.SessionEndGrace * 2);
        await Task.Delay(50);

        await Assert.That(EndCount).IsEqualTo(0);
    }

    /// <summary>What a person disabled gets no end either, however the session stopped.</summary>
    [Test]
    public async Task A_session_disabled_inside_the_grace_gets_no_end() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        Exited.Add(4242);
        recorder.FinishExitedProcesses();
        await WaitHarness.PollUntilAsync(() => recorder.AwaitsEnd(source.Key));
        DisabledSessions.Mark(Session, Config.Root);
        await recorder.DisableSessionAsync(Session, CancellationToken.None).WithinDeadlineAsync("the disable");

        time.Advance(DaemonRecorder.SessionEndGrace * 2);
        await Task.Delay(50);

        await Assert.That(EndCount).IsEqualTo(0);
    }

    /// <summary>A session resumed in a new process while this one exits runs on there: its hook claimed it, and its next
    /// hook may be minutes off. The source follows the new process instead of ending, and ends when that one does.</summary>
    [Test]
    public async Task A_session_resumed_in_another_process_is_followed_rather_than_ended() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        ClaimNote(4343);
        Exited.Add(4242);
        recorder.FinishExitedProcesses();

        await Assert.That(recorder.Holds(source.Key)).IsTrue();
        await Assert.That(List.Load().Single().Process).IsEqualTo(new ProcessIncarnation(4343, "test:claude"));
        await Assert.That(Transport.DrainCompletes).IsEqualTo(0);

        Exited.Add(4343);
        recorder.FinishExitedProcesses();
        await Stepping(time, () => EndCount == 1);
    }

    /// <summary>A session resumed in a new process inside the grace runs on there, and that process's SessionStart found
    /// the claim fresh and started nothing. The daemon finds it before the grace is out and follows it, with no end.</summary>
    [Test]
    public async Task A_session_resumed_inside_the_grace_is_followed_with_no_end_posted() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var path   = Transcript(Prompt, Reply);
        var source = Terminal(path);
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        Exited.Add(4242);
        recorder.FinishExitedProcesses();
        await WaitHarness.PollUntilAsync(() => recorder.AwaitsEnd(source.Key));
        ClaimNote(4343);
        await File.AppendAllTextAsync(path, $"{Reply}\n");
        var noted = time.GetUtcNow();

        await Stepping(time, () => recorder.Holds(source.Key) || time.GetUtcNow() - noted >= DaemonRecorder.SessionEndGrace);
        await Assert.That(time.GetUtcNow() - noted).IsLessThan(DaemonRecorder.SessionEndGrace);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Any(batch => batch.LineNumbers.Contains(2)));
        time.Advance(DaemonRecorder.SessionEndGrace * 2);
        await Task.Delay(50);

        await Assert.That(EndCount).IsEqualTo(0);
        await Assert.That(recorder.Holds(source.Key)).IsTrue();
        await Assert.That(List.Load().Single().Process).IsEqualTo(new ProcessIncarnation(4343, "test:claude"));
    }

    /// <summary>The claim went to another daemon while the end waited, so the resumed run is that daemon's to record, and
    /// this one neither follows it nor posts an end.</summary>
    [Test]
    public async Task A_resume_inside_the_grace_is_not_followed_onto_a_claim_another_took() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        Exited.Add(4242);
        recorder.FinishExitedProcesses();
        await WaitHarness.PollUntilAsync(() => recorder.AwaitsEnd(source.Key));
        Claims.Release(source.Key);
        Claims.TryTake(source.Key, "daemon-b", isHosted: false);
        ClaimNote(4343);

        await Stepping(time, () => !recorder.AwaitsEnd(source.Key));
        time.Advance(DaemonRecorder.SessionEndGrace * 2);
        await Task.Delay(50);

        await Assert.That(recorder.Holds(source.Key)).IsFalse();
        await Assert.That(Claims.Holder(source.Key)).IsEqualTo("daemon-b");
        await Assert.That(EndCount).IsEqualTo(0);
    }

    /// <summary>The resumed process can end inside the grace as well. Its hook is that process's own, not the exited one's
    /// late hook: the source follows it and makes the final drain of the lines it wrote, and the hook posts the end.</summary>
    [Test]
    public async Task A_resumed_processes_session_end_inside_the_grace_drains_its_own_lines() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var path   = Transcript(Prompt, Reply);
        var source = Terminal(path);
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        Exited.Add(4242);
        recorder.FinishExitedProcesses();
        await WaitHarness.PollUntilAsync(() => recorder.AwaitsEnd(source.Key));
        await File.AppendAllTextAsync(path, $"{Reply}\n");

        var resumed = new ProcessIncarnation(4343, "test:claude");
        await Assert.That(await recorder.FinishForHookAsync(source.Key, resumed, CancellationToken.None).WithinDeadlineAsync("the resumed run's finish")).IsTrue();

        await Assert.That(Transport.Batches.Any(batch => batch.LineNumbers.Contains(2))).IsTrue();
        await Assert.That(Transport.DrainCompletes).IsEqualTo(2);

        time.Advance(DaemonRecorder.SessionEndGrace * 2);
        await Task.Delay(50);

        await Assert.That(EndCount).IsEqualTo(0);
    }

    /// <summary>A hook's finish declined because the source hasn't started takes nothing from its process's exit, which
    /// still ends the source.</summary>
    [Test]
    public async Task A_declined_hook_finish_leaves_the_end_to_the_processes_exit() {
        await using var recorder = NewRecorder();
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.ClaimAsync(source, CancellationToken.None);

        await Assert.That(await recorder.FinishForHookAsync(source.Key, source.Process, CancellationToken.None)).IsFalse();

        recorder.StartForAgent(source);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);
        Exited.Add(4242);
        recorder.FinishExitedProcesses();

        await WaitHarness.PollUntilAsync(() => Transport.DrainCompletes == 1);
    }

    /// <summary>The hook claimed the end and was killed before its post landed. Its claim never says posted, so once it
    /// is older than any hook takes the daemon posts the end itself; the server appends nothing for a second one.</summary>
    [Test]
    public async Task A_hooks_claim_left_unposted_is_taken_over_by_the_daemon() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        Exited.Add(4242);
        recorder.FinishExitedProcesses();
        await WaitHarness.PollUntilAsync(() => recorder.AwaitsEnd(source.Key));
        SessionEndRecord.TryClaim(Paths, Session, source.Process!, time.GetUtcNow());

        await Stepping(time, () => EndCount == 1 && List.Load().Count == 0);
    }

    /// <summary>A daemon killed between its claim of the end and its post left the claim, the list entry and its own
    /// recorder claim. The next boot finds the claim old and unposted, and posts the end.</summary>
    [Test]
    public async Task A_daemon_killed_between_its_claim_and_its_post_posts_the_end_at_the_next_boot() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Terminal(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: false);
        Exited.Add(4242);
        SessionEndRecord.TryClaim(Paths, Session, source.Process!, time.GetUtcNow() - TimeSpan.FromSeconds(5));

        await using var recorder = NewRecorder(time: time);
        await recorder.RestoreAsync(InScope, CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => false);

        await Stepping(time, () => EndCount == 1 && List.Load().Count == 0);
    }

    /// <summary>The exited process's continuation can land after the source has followed a resumed process. That run is
    /// over and the session went on, so the finish ends nothing, and the resumed run goes on recording.</summary>
    [Test]
    public async Task A_late_hook_from_the_exited_process_after_a_follow_leaves_the_resumed_run_recording() {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        Exited.Add(4242);
        recorder.FinishExitedProcesses();
        await WaitHarness.PollUntilAsync(() => recorder.AwaitsEnd(source.Key));
        ClaimNote(4343);
        await Stepping(time, () => recorder.Holds(source.Key));
        var drains = Transport.DrainCompletes;

        await Assert.That(await recorder.FinishForHookAsync(source.Key, source.Process, CancellationToken.None).WithinDeadlineAsync("the late hook's finish")).IsTrue();

        await Assert.That(recorder.Holds(source.Key)).IsTrue();
        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
        await Assert.That(Transport.DrainCompletes).IsEqualTo(drains);
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, source.Process!, time.GetUtcNow())).IsEqualTo(SessionEndTurn.Done);
    }

    /// <summary>A source the exit followed at once to a resumed process is that process's, and the exited one's late
    /// hook finishes nothing of it.</summary>
    [Test]
    public async Task A_late_hook_from_the_exited_process_after_an_exit_time_follow_leaves_the_source_recording() {
        await using var recorder = NewRecorder();
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        ClaimNote(4343);
        Exited.Add(4242);
        recorder.FinishExitedProcesses();

        await Assert.That(await recorder.FinishForHookAsync(source.Key, source.Process, CancellationToken.None).WithinDeadlineAsync("the late hook's finish")).IsTrue();
        await Assert.That(recorder.Holds(source.Key)).IsTrue();
        await Assert.That(Transport.DrainCompletes).IsEqualTo(0);
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, source.Process!, DateTimeOffset.UtcNow)).IsEqualTo(SessionEndTurn.Done);
    }

    /// <summary>A follow means the exited run is over and the session went on, so that run owes no end. A stop that
    /// refuses the follow leaves the source for the next boot, which then posts none for it.</summary>
    [Test]
    public async Task A_follow_refused_by_a_stop_still_leaves_the_exited_run_no_end() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Terminal(Transcript(Prompt, Reply));

        await using (var recorder = NewRecorder(time: time)) {
            await recorder.TryRecordAsync(source, CancellationToken.None);
            await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

            Exited.Add(4242);
            recorder.FinishExitedProcesses();
            await WaitHarness.PollUntilAsync(() => recorder.AwaitsEnd(source.Key));
            await recorder.StopAsync(_ => false, CancellationToken.None).WithinDeadlineAsync("the stop");
            await recorder.FinishForHookAsync(source.Key, new ProcessIncarnation(4343, "test:claude"), CancellationToken.None).WithinDeadlineAsync("the resumed run's finish");
        }

        Exited.Add(4343);
        await using var next = NewRecorder(time: time);
        await next.RestoreAsync(InScope, CancellationToken.None);
        await next.SettleRestoredAsync(_ => false);
        await Stepping(time, () => List.Load().Count == 0);
        time.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(50);

        await Assert.That(EndCount).IsEqualTo(0);
    }

    /// <summary>A daemon stopping inside the grace hasn't decided the end, so it keeps the claim and the entry, and the
    /// next boot finds the process gone and posts it.</summary>
    [Test]
    public async Task A_daemon_stopping_inside_the_grace_leaves_the_end_to_the_next_boot() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var claims = new RecorderClaims(Paths.Directory, time);
        var source = Terminal(Transcript(Prompt, Reply));

        await using (var recorder = NewRecorder(time: time)) {
            await recorder.TryRecordAsync(source, CancellationToken.None);
            await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

            Exited.Add(4242);
            recorder.FinishExitedProcesses();
            await WaitHarness.PollUntilAsync(() => Transport.DrainCompletes == 1);
            await recorder.StopAsync(_ => false, CancellationToken.None).WithinDeadlineAsync("the stop");
        }

        await Assert.That(EndCount).IsEqualTo(0);
        await Assert.That(claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(List.Load().Select(s => s.Key)).IsEquivalentTo([source.Key]);

        await using var next = NewRecorder(time: time);
        await next.RestoreAsync(InScope, CancellationToken.None);
        await next.SettleRestoredAsync(_ => false);

        await Stepping(time, () => EndCount == 1 && List.Load().Count == 0);
    }

    /// <summary>The hook's finish inside the grace leaves the end to the hook, which claims that process's end before it
    /// posts and marks it posted after. A daemon that stops and boots again before deciding finds it posted and posts no
    /// second end.</summary>
    [Test]
    public async Task An_end_the_hook_claimed_before_a_restart_is_not_posted_again() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Terminal(Transcript(Prompt, Reply));

        await using (var recorder = NewRecorder(time: time)) {
            await recorder.TryRecordAsync(source, CancellationToken.None);
            await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

            Exited.Add(4242);
            recorder.FinishExitedProcesses();
            await WaitHarness.PollUntilAsync(() => recorder.AwaitsEnd(source.Key));
            await recorder.FinishForHookAsync(source.Key, source.Process, CancellationToken.None).WithinDeadlineAsync("the hook's finish");
            SessionEndRecord.TryClaim(Paths, Session, source.Process!, time.GetUtcNow());
            SessionEndRecord.MarkPosted(Paths, Session, source.Process!, time.GetUtcNow());
            await recorder.StopAsync(_ => false, CancellationToken.None).WithinDeadlineAsync("the stop");
        }

        await using var next = NewRecorder(time: time);
        await next.RestoreAsync(InScope, CancellationToken.None);
        await next.SettleRestoredAsync(_ => false);
        await Stepping(time, () => List.Load().Count == 0);

        await Assert.That(EndCount).IsEqualTo(0);
    }

    /// <summary>A claim gone while the daemon was down was taken by a hook, which recorded the session from there and posted
    /// its end. The daemon finishes what it left, but an end of its own would be a false one, as late as its next boot.</summary>
    [Test]
    public async Task A_restored_terminal_source_whose_claim_was_taken_gets_no_end() {
        var source = Terminal(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Exited.Add(4242);

        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var recorder = NewRecorder(time: time);
        await recorder.RestoreAsync(InScope, CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => false);

        await Stepping(time, () => List.Load().Count == 0 && recorder.ActiveKeys.Count == 0);
        time.Advance(DaemonRecorder.SessionEndGrace * 2);
        await Task.Delay(50);

        await Assert.That(EndCount).IsEqualTo(0);
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>A boot finds a terminal source's process gone and its session claimed by another process, which resumed it
    /// while the daemon was down. The source follows that process rather than ending.</summary>
    [Test]
    public async Task A_restored_session_resumed_in_another_process_is_followed() {
        var source = Terminal(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: false);
        Exited.Add(4242);
        ClaimNote(4343);

        await using var recorder = NewRecorder(time: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await recorder.RestoreAsync(InScope, CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => false);

        await Assert.That(recorder.Holds(source.Key)).IsTrue();
        await Assert.That(List.Load().Single().Process).IsEqualTo(new ProcessIncarnation(4343, "test:claude"));
    }

    /// <summary>The daemon died inside the grace and came back: what still runs is picked up from the server's position,
    /// and what ended while it was away gets the tail it missed and is finished.</summary>
    [Test]
    public async Task A_restart_picks_up_a_terminal_source_still_running_and_finishes_one_that_ended() {
        const string ended = "fedcba9876543210fedcba9876543210";
        var running = Terminal(Transcript(Prompt, Reply));
        var gone    = Terminal(Tmp.CreateFile("gone.jsonl", $"{Prompt}\n"), ended, pid: 4343);
        List.TryAdd(running);
        List.TryAdd(gone);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(running.Key, Name, isHosted: false);
        Claims.TryTake(gone.Key, Name, isHosted: false);
        Exited.Add(4343);

        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var claims = new RecorderClaims(Paths.Directory, time);
        await using var recorder = NewRecorder(time: time);
        await recorder.RestoreAsync(InScope, CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => false);

        await Stepping(time, () => claims.Read(gone.Key) is RecorderClaimState.None && recorder.ActiveKeys.Count == 1 && List.Load().Count == 1);
        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([running.Key]);
        await Assert.That(List.Load().Select(s => s.Key)).IsEquivalentTo([running.Key]);
        await Assert.That(Transport.Batches.Any(batch => batch.SessionId == ended)).IsTrue();
        await Assert.That(Ends.Select(end => end.Key)).IsEquivalentTo([gone.Key]);
    }

    /// <summary>A terminal session outlives a daemon that stops, so the next boot picks it up. Its claim stays for the
    /// grace, which is what keeps a hook from starting a watcher across a restart. One whose process had already gone
    /// without anyone seeing it stays too, so the next boot posts the end nobody else will.</summary>
    [Test]
    public async Task A_shutdown_keeps_every_terminal_source_for_the_next_boot() {
        await using var recorder = NewRecorder();
        var running = Terminal(Transcript(Prompt, Reply));
        var ended   = Terminal(Tmp.CreateFile("ended.jsonl", $"{Prompt}\n"), "fedcba9876543210fedcba9876543210", pid: 4343);
        await recorder.TryRecordAsync(running, CancellationToken.None);
        await recorder.TryRecordAsync(ended, CancellationToken.None);
        Exited.Add(4343);

        await recorder.StopAsync(_ => false, CancellationToken.None).WithinDeadlineAsync("the stop");

        await Assert.That(Claims.Read(running.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(Claims.Read(ended.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(List.Load().Select(s => s.Key)).IsEquivalentTo([running.Key, ended.Key]);
        await Assert.That(EndCount).IsEqualTo(0);
    }

    /// <summary>A hosted source with no agent coming back is still let go at a shutdown.</summary>
    [Test]
    public async Task A_shutdown_lets_go_of_a_hosted_source_whose_agent_does_not_come_back() {
        await using var recorder = NewRecorder();
        var hosted = Source(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(hosted, CancellationToken.None);

        await recorder.StopAsync(_ => false, CancellationToken.None).WithinDeadlineAsync("the stop");

        await Assert.That(Claims.Read(hosted.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>With the server out past the grace, a terminal session goes to a watcher, which may reach a server this
    /// daemon can't, while a hosted one stays for its stamps.</summary>
    [Test]
    public async Task Only_a_terminal_source_lets_its_claim_go_stale_while_the_server_is_out() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var claims = new RecorderClaims(Paths.Directory, time);
        Transport.IsConnected     = false;
        Transport.ConnectFailures = 1000;
        await using var recorder = NewRecorder(time: time);
        var hosted   = Source(Transcript(Prompt, Reply));
        var terminal = Terminal(Tmp.CreateFile("terminal.jsonl", $"{Prompt}\n"), "fedcba9876543210fedcba9876543210");
        await recorder.TryRecordAsync(hosted, CancellationToken.None);
        await recorder.TryRecordAsync(terminal, CancellationToken.None);
        var start = time.GetUtcNow();

        await WaitHarness.PollUntilAsync(() => {
            time.Advance(TimeSpan.FromSeconds(1));

            return time.GetUtcNow() - start > RecorderClaims.Grace * 2 && claims.Read(terminal.Key) is RecorderClaimState.DaemonStale;
        });

        await Assert.That(claims.Read(hosted.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    /// <summary>Two daemons share one directory. The one that slept finds its source taken by the other: it stops sending
    /// with no final drain, and neither beats nor lets go of the claim that is now the other's.</summary>
    [Test]
    public async Task A_source_another_daemon_took_stops_here_and_its_claim_is_left_alone() {
        await using var recorder = NewRecorder();
        var source = Terminal(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);

        Claims.Release(source.Key);
        Claims.TryTake(source.Key, "daemon-b", isHosted: false);

        await WaitHarness.PollUntilAsync(() => recorder.ActiveKeys.Count == 0 && List.Load().Count == 0);
        await Assert.That(Claims.Holder(source.Key)).IsEqualTo("daemon-b");
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(Transport.DrainCompletes).IsEqualTo(0);
    }

    /// <summary>Another daemon took the claim while this one's agent was starting. The launch failing here lets go of
    /// nothing that daemon holds.</summary>
    [Test]
    public async Task A_launch_that_never_ran_leaves_a_claim_another_daemon_took() {
        await using var recorder = NewRecorder();
        var source = Source(Transcript(Prompt));
        await Assert.That(await recorder.ClaimAsync(source, CancellationToken.None)).IsTrue();

        Claims.Release(source.Key);
        Claims.TryTake(source.Key, "daemon-b", isHosted: true);
        recorder.ReleaseClaim(source);

        await Assert.That(Claims.Holder(source.Key)).IsEqualTo("daemon-b");
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>A claim that can't be read may be this daemon's own, and a launch that never ran lets go of it.</summary>
    [Test]
    public async Task A_launch_that_never_ran_lets_go_of_a_claim_that_cannot_be_read() {
        await using var recorder = NewRecorder();
        var source = Source(Transcript(Prompt));
        await recorder.ClaimAsync(source, CancellationToken.None);

        await File.WriteAllTextAsync(Claims.ClaimFile(source.Key), "garbled");
        recorder.ReleaseClaim(source);

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
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
        await recorder.SettleRestoredAsync(_ => false);

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
        await recorder.SettleRestoredAsync(_ => false);

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
        await recorder.SettleRestoredAsync(_ => false);

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
        await recorder.RestoreAsync(LeftToHooks(), CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => false);

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
        await recorder.RestoreAsync(LeftToHooks(), CancellationToken.None);

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
        await recorder.SettleRestoredAsync(_ => false);

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

        await recorder.RestoreAsync(LeftToHooks(), CancellationToken.None);

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

    bool MarkedForImport => new TranscriptSpool(Config.Root, TimeProvider.System, Url).NeedsImport(Session);

    RecordedSource LeftBehind() {
        var source = Source(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);

        return source;
    }

    /// <summary>A hard-killed daemon's tail with no agent to come back: no hook arrives and nothing is left to drain it,
    /// so only the marker says it was lost.</summary>
    [Test]
    public async Task A_source_let_go_whose_agent_is_gone_is_marked_for_import() {
        LeftBehind();

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(LeftToHooks(), CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => false);

        await Assert.That(MarkedForImport).IsTrue();
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>An import uploads the transcript, so a mark would hand the scope's exclusion to the next person to run one.</summary>
    [Test]
    public async Task A_source_the_scope_refuses_is_not_marked_for_import() {
        LeftBehind();

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(LeftToHooks(mayImport: false), CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => false);

        await Assert.That(MarkedForImport).IsFalse();
        await Assert.That(List.Load()).IsEmpty();
    }

    [Test]
    public async Task A_source_let_go_on_a_server_without_recording_is_marked_for_import() {
        LeftBehind();
        var probe = new RecordingProbe();
        probe.Report(false);

        await using var recorder = NewRecorder(probe: probe);
        await recorder.RestoreAsync(InScope, CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => false);

        await Assert.That(MarkedForImport).IsTrue();
    }

    [Test]
    public async Task A_source_let_go_with_recording_off_is_marked_for_import() {
        LeftBehind();

        await using var recorder = NewRecorder(on: false);
        await recorder.RestoreAsync(InScope, CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => false);

        await Assert.That(MarkedForImport).IsTrue();
    }

    /// <summary>Its agent's next hook hands it over, and that recorder starts from the server's position.</summary>
    [Test]
    public async Task A_source_let_go_whose_agent_came_back_is_not_marked() {
        LeftBehind();

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(LeftToHooks(), CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => true);

        await Assert.That(MarkedForImport).IsFalse();
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>With no verdict there's no mark, and dropping the entry would leave nothing to say a tail was lost. The
    /// claim still goes, for the hooks of what resumes. A config that reads at the next boot decides.</summary>
    [Test]
    public async Task A_source_under_an_unreadable_config_waits_for_the_next_boot() {
        var source = LeftBehind();

        await using (var first = NewRecorder()) {
            await first.RestoreAsync(() => Task.FromResult(RecordingScope.Unknown), CancellationToken.None);
            await first.SettleRestoredAsync(_ => false);
        }

        await Assert.That(MarkedForImport).IsFalse();
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load().Select(left => left.Key)).IsEquivalentTo([source.Key]);

        await using var second = NewRecorder();
        await second.RestoreAsync(LeftToHooks(), CancellationToken.None);
        await second.SettleRestoredAsync(_ => false);

        await Assert.That(MarkedForImport).IsTrue();
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>Nothing is unsent, and a marker re-posts on every drain pass until it ages out.</summary>
    [Test]
    public async Task A_source_drained_at_a_graceful_stop_is_not_marked() {
        var source = Source(Transcript(Prompt, Reply));

        await using (var stopping = NewRecorder()) {
            await stopping.TryRecordAsync(source, CancellationToken.None);
            await stopping.StopAsync(_ => true, CancellationToken.None).WithinDeadlineAsync("the stop");
        }

        await Assert.That(List.Load().Single().Drained).IsTrue();

        await using var next = NewRecorder();
        await next.RestoreAsync(LeftToHooks(), CancellationToken.None);
        await next.SettleRestoredAsync(_ => false);

        await Assert.That(MarkedForImport).IsFalse();
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>A hold ends the final drain with no failure, and the line and everything behind it went nowhere.</summary>
    [Test]
    public async Task A_source_held_at_a_redaction_timeout_is_not_drained() {
        var source = Source(Transcript(Prompt, HeldLine));

        await using var stopping = NewRecorder(time: new SwitchableRedactionClock { Exhausted = true });
        await stopping.TryRecordAsync(source, CancellationToken.None);
        await stopping.StopAsync(_ => true, CancellationToken.None).WithinDeadlineAsync("the stop");

        await Assert.That(List.Load().Single().Drained).IsFalse();
    }

    void Disable() => DisabledSessions.Mark(Session, Config.Root);

    /// <summary>A recorder stuck where no token reaches, for as long as <see cref="Stuck.Release"/> isn't called.</summary>
    sealed class Stuck {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task Prepare() {
            _entered.TrySetResult();

            return _release.Task;
        }

        public void Release() => _release.SetResult();
    }

    /// <summary>A restart must not undo what the person did while this daemon was down.</summary>
    [Test]
    public async Task A_disabled_session_is_not_restored() {
        var source = Source(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);
        Disable();

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(InScope, CancellationToken.None).WithinDeadlineAsync("the restore");

        await Assert.That(recorder.ActiveKeys).IsEmpty();
        await Assert.That(Transport.Connects).IsEqualTo(0);
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task An_excluded_session_is_still_restored() {
        var source = Source(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);
        DisabledSessions.Exclude(Session, Config.Root);

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(InScope, CancellationToken.None).WithinDeadlineAsync("the restore");

        await Assert.That(recorder.ActiveKeys).IsNotEmpty();
    }

    /// <summary>A mark invites the import to send again what the person deleted, so the settling that writes one for any
    /// other source writes none here, and the entry goes as it would.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_disabled_session_leaves_no_import_mark_and_no_list_entry(bool leftToHooks) {
        var source = Source(Transcript(Prompt, Reply));
        List.TryAdd(source);
        Disable();
        var spool = new TranscriptSpool(Config.Root, TimeProvider.System, Url);
        spool.MarkNeedsImport(Session, "left by an earlier stop");

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(leftToHooks ? LeftToHooks() : InScope, CancellationToken.None).WithinDeadlineAsync("the restore");
        await recorder.SettleRestoredAsync(_ => false).WithinDeadlineAsync("the settling");

        await Assert.That(MarkedForImport).IsFalse();
        await Assert.That(List.Load()).IsEmpty();
    }

    [Test]
    public async Task A_disabled_session_is_not_taken_when_handed_over() {
        Disable();

        await using var recorder = NewRecorder();

        await Assert.That(await recorder.TryRecordAsync(Source(Transcript(Prompt)), CancellationToken.None)).IsFalse();
        await Assert.That(await recorder.ClaimAsync(Source(Transcript(Prompt)), CancellationToken.None)).IsFalse();
        await Assert.That(Connections).IsEqualTo(0);
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>A launch claims ahead of its agent, and the person can disable the session in between.</summary>
    [Test]
    public async Task A_session_disabled_behind_its_claim_never_starts() {
        var source = Source(Transcript(Prompt));
        await using var recorder = NewRecorder();
        await Assert.That(await recorder.ClaimAsync(source, CancellationToken.None)).IsTrue();

        Disable();
        recorder.StartForAgent(source);

        await Assert.That(recorder.ActiveKeys).IsEmpty();
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load()).IsEmpty();
    }

    [Test]
    public async Task A_disable_of_a_session_held_here_says_it_stopped() {
        var source = Source(Transcript(Prompt, Reply));
        await using var recorder = NewRecorder();
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);
        Disable();

        await Assert.That(await recorder.DisableSessionAsync(Session, CancellationToken.None).WithinDeadlineAsync("the disable")).IsEqualTo(RecordingDisable.Stopped);

        await Assert.That(recorder.ActiveKeys).IsEmpty();
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load()).IsEmpty();
        await Assert.That(MarkedForImport).IsFalse();
        await Assert.That(Transport.DrainCompletes).IsEqualTo(0);
        await Assert.That(Transport.Refused).IsEqualTo(0);
    }

    [Test]
    public async Task A_disable_of_a_session_held_elsewhere_says_so() {
        var source = Source(Transcript(Prompt));
        await using var recorder = NewRecorder();
        await recorder.TryRecordAsync(source, CancellationToken.None);

        await Assert.That(await recorder.DisableSessionAsync("fedcba9876543210fedcba9876543210", CancellationToken.None)).IsEqualTo(RecordingDisable.NotHeld);

        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
        await recorder.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");
    }

    [Test]
    public async Task A_disable_stops_the_sessions_subagents_and_no_other_session() {
        const string other = "fedcba9876543210fedcba9876543210";
        var root     = Source(Tmp.CreateFile("root.jsonl", $"{Prompt}\n"));
        var subagent = Source(Tmp.CreateFile("sub.jsonl", $"{Prompt}\n"), "agent-b");
        var bystander = root with { SessionId = other, TranscriptPath = Tmp.CreateFile("other.jsonl", $"{Prompt}\n") };
        await using var recorder = NewRecorder();
        await recorder.TryRecordAsync(root, CancellationToken.None);
        await recorder.TryRecordAsync(subagent, CancellationToken.None);
        await recorder.TryRecordAsync(bystander, CancellationToken.None);

        await recorder.DisableSessionAsync(Session, CancellationToken.None).WithinDeadlineAsync("the disable");

        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([bystander.Key]);
        await Assert.That(List.Load().Select(left => left.Key)).IsEquivalentTo([bystander.Key]);
        await recorder.FinishAsync(bystander.Key, CancellationToken.None).WithinDeadlineAsync("the finish");
    }

    /// <summary>An older <c>kcap disable</c> leaves the claim and sends no frame. The recorder stops on the marker, and with
    /// nothing else coming the claim, the entry and anything spooled are this daemon's to clear.</summary>
    [Test]
    public async Task A_marker_with_no_frame_stops_a_source_and_clears_up_behind_it() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(Transcript(Prompt, Reply));
        var spool  = new TranscriptSpool(Config.Root, TimeProvider.System, Url);
        await using var recorder = NewRecorder(time: time);
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Transport.Batches.Count > 0);
        spool.Append(Session, """{"lines":["a"],"line_numbers":[0]}""");

        Disable();

        await WaitHarness.PollUntilAsync(() => {
            time.Advance(TimeSpan.FromSeconds(1));

            return recorder.ActiveKeys.Count == 0 && List.Load().Count == 0 && !spool.HasBacklog(Session);
        });
        await WaitHarness.PollUntilAsync(() => Claims.Read(source.Key) is RecorderClaimState.None);

        await Assert.That(Transport.DrainCompletes).IsEqualTo(0);
        await Assert.That(MarkedForImport).IsFalse();
    }

    /// <summary>Claimed ahead of a launch with no recorder yet, so nothing but this lets the claim go.</summary>
    [Test]
    public async Task A_disable_lets_go_of_a_source_not_started_yet() {
        var source = Source(Transcript(Prompt));
        await using var recorder = NewRecorder();
        await recorder.ClaimAsync(source, CancellationToken.None);

        await recorder.DisableSessionAsync(Session, CancellationToken.None).WithinDeadlineAsync("the disable");

        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(List.Load()).IsEmpty();
        recorder.StartForAgent(source);
        await Assert.That(recorder.ActiveKeys).IsEmpty();
    }

    /// <summary>A recorder stuck where no token reaches still can't send when it wakes, but nothing here can say it has
    /// stopped, so the command must not be told it has.</summary>
    [Test]
    public async Task A_source_stuck_where_no_token_reaches_answers_within_the_bound_and_sends_nothing_after() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var stuck  = new Stuck();
        var source = Source(Transcript(Prompt, Reply));
        await using var recorder = NewRecorder(time: time, prepare: stuck.Prepare);
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await stuck.Entered.WithinDeadlineAsync("the recorder to get stuck");

        var disable = recorder.DisableSessionAsync(Session, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Claims.Read(source.Key) is RecorderClaimState.None);
        time.Advance(DaemonRecorder.DisableBound);

        await Assert.That(await disable.WithinDeadlineAsync("the answer")).IsEqualTo(RecordingDisable.StillStopping);
        await Assert.That(List.Load()).IsEmpty();

        // What comes for it next doesn't wait on a source that may never move.
        await Assert.That(await recorder.TryRecordAsync(source, CancellationToken.None).WithinDeadlineAsync("the hand-over")).IsFalse();
        await recorder.FinishAgentAsync(source.AgentId!, TimeSpan.FromMinutes(1)).WithinDeadlineAsync("the agent's end");

        stuck.Release();
        await WaitHarness.PollUntilAsync(() => recorder.ActiveKeys.Count == 0);

        await Assert.That(Transport.Batches).IsEmpty();
        await Assert.That(MarkedForImport).IsFalse();
    }

    /// <summary>A disable lets go of this daemon's claims and no other's. Stuck short of its next look, the recorder has not
    /// yet seen that another daemon took its source.</summary>
    [Test]
    public async Task A_disable_leaves_a_claim_another_daemon_took() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var stuck  = new Stuck();
        var source = Source(Transcript(Prompt, Reply));
        await using var recorder = NewRecorder(time: time, prepare: stuck.Prepare);
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await stuck.Entered.WithinDeadlineAsync("the recorder to get stuck");

        Claims.Release(source.Key);
        Claims.TryTake(source.Key, "daemon-b", isHosted: true);
        var disable = recorder.DisableSessionAsync(Session, CancellationToken.None);
        time.Advance(DaemonRecorder.DisableBound);
        await disable.WithinDeadlineAsync("the answer");
        stuck.Release();
        await WaitHarness.PollUntilAsync(() => recorder.ActiveKeys.Count == 0);

        await Assert.That(Claims.Holder(source.Key)).IsEqualTo("daemon-b");
    }

    /// <summary>A source that wakes long after the disable can reach the spool between its check and its write.</summary>
    [Test]
    public async Task What_a_stuck_source_spools_on_its_way_out_is_discarded() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var stuck  = new Stuck();
        var source = Source(Transcript(Prompt, Reply));
        var spool  = new TranscriptSpool(Config.Root, TimeProvider.System, Url);
        await using var recorder = NewRecorder(time: time, prepare: stuck.Prepare);
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await stuck.Entered.WithinDeadlineAsync("the recorder to get stuck");

        var disable = recorder.DisableSessionAsync(Session, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Claims.Read(source.Key) is RecorderClaimState.None);
        time.Advance(DaemonRecorder.DisableBound);
        await disable.WithinDeadlineAsync("the answer");

        spool.Append(Session, """{"lines":["a"],"line_numbers":[0]}""");
        stuck.Release();
        await WaitHarness.PollUntilAsync(() => !spool.HasBacklog(Session));

        await Assert.That(spool.HasBacklog(Session)).IsFalse();
    }

    /// <summary>What the server hasn't answered can still land, and the person is told so rather than that nothing did.</summary>
    [Test]
    public async Task A_send_the_server_never_answered_is_said_to_be_in_flight() {
        var source = Source(Transcript(Prompt));
        await using var recorder = NewRecorder();
        await recorder.TryRecordAsync(source, CancellationToken.None);
        Transport.SilenceAnswer = Task.FromResult(false);

        await Assert.That(await recorder.DisableSessionAsync(Session, CancellationToken.None).WithinDeadlineAsync("the disable")).IsEqualTo(RecordingDisable.SendInFlight);
    }

    /// <summary>The lane behind a key's gate can hold it for a minute, and the command only waits seconds.</summary>
    [Test]
    public async Task A_disable_does_not_wait_for_the_gate_of_a_key_being_picked_up() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(Transcript(Prompt));
        var held   = new RecorderClaims(Paths.Directory, time);
        Directory.CreateDirectory(Paths.Directory);
        held.TryTake(source.Key, "another-daemon", isHosted: true);
        List.TryAdd(source);

        await using var recorder = NewRecorder(time: time);
        var restore = recorder.RestoreAsync(InScope, CancellationToken.None);

        await Assert.That(await recorder.DisableSessionAsync(Session, CancellationToken.None).WithinDeadlineAsync("the disable")).IsEqualTo(RecordingDisable.NotHeld);

        Disable();
        time.Advance(TimeSpan.FromSeconds(1));
        await restore.WithinDeadlineAsync("the restore to read the marker");
        await Assert.That(recorder.ActiveKeys).IsEmpty();
        await Assert.That(List.Load()).IsEmpty();
    }

    /// <summary>The list entry goes with the stop: a shutdown behind it must not write one back.</summary>
    [Test]
    public async Task A_shutdown_behind_a_disable_writes_no_list_entry() {
        var stuck  = new Stuck();
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = Source(Transcript(Prompt, Reply));
        await using var recorder = NewRecorder(time: time, prepare: stuck.Prepare);
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await stuck.Entered.WithinDeadlineAsync("the recorder to get stuck");

        var disable = recorder.DisableSessionAsync(Session, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => Claims.Read(source.Key) is RecorderClaimState.None);
        await recorder.StopAsync(_ => true, new CancellationToken(canceled: true)).WithinDeadlineAsync("the stop");
        stuck.Release();
        await WaitHarness.PollUntilAsync(() => recorder.ActiveKeys.Count == 0);
        time.Advance(DaemonRecorder.DisableBound);
        await disable.WithinDeadlineAsync("the disable");

        await Assert.That(List.Load()).IsEmpty();
        await Assert.That(MarkedForImport).IsFalse();
    }

    /// <summary>A stuck source never reached its final drain, so its tail is what the mark is for.</summary>
    [Test]
    public async Task A_source_stuck_at_a_stop_is_marked() {
        var stuck  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Source(Transcript(Prompt, Reply));

        await using (var stopping = NewRecorder(prepare: () => stuck.Task)) {
            await stopping.TryRecordAsync(source, CancellationToken.None);
            await stopping.StopAsync(_ => true, new CancellationToken(canceled: true)).WithinDeadlineAsync("the stop");
            await Assert.That(List.Load().Single().Drained).IsFalse();

            await using var next = NewRecorder();
            await next.RestoreAsync(LeftToHooks(), CancellationToken.None);
            await next.SettleRestoredAsync(_ => false);

            await Assert.That(MarkedForImport).IsTrue();
            stuck.SetResult();
        }
    }

    /// <summary>The next tail is unsent again, so a kill behind this restore must not read as drained.</summary>
    [Test]
    public async Task A_restored_source_reads_as_undrained() {
        var source = Source(Transcript(Prompt, Reply)) with { Drained = true };
        List.TryAdd(source);
        Directory.CreateDirectory(Paths.Directory);
        Claims.TryTake(source.Key, Name, isHosted: true);

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(InScope, CancellationToken.None);

        await Assert.That(List.Load().Single().Drained).IsFalse();
    }

    /// <summary>The entry stays until then, so a kill ahead of the settling leaves the next boot the same decision.</summary>
    [Test]
    public async Task A_source_let_go_stays_on_the_list_until_the_settling() {
        var source = LeftBehind();

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(LeftToHooks(), CancellationToken.None);

        await Assert.That(List.Load().Select(left => left.Key)).IsEquivalentTo([source.Key]);
        await Assert.That(Claims.Read(source.Key)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>A hook got there first: the source is recording, and its entry is that recorder's.</summary>
    [Test]
    public async Task A_source_handed_back_ahead_of_the_settling_is_left_recording() {
        var source = LeftBehind();

        await using var recorder = NewRecorder();
        await recorder.RestoreAsync(LeftToHooks(), CancellationToken.None);
        await recorder.TryRecordAsync(source, CancellationToken.None);
        await recorder.SettleRestoredAsync(_ => false);

        await Assert.That(MarkedForImport).IsFalse();
        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
        await Assert.That(List.Load().Select(left => left.Key)).IsEquivalentTo([source.Key]);
    }

    /// <summary>The server stops a source through what it no longer has. Left running, each would re-register and record
    /// with no stop path, so a definite no ends them, and with their claims gone hooks take over.</summary>
    [Test]
    public async Task A_definite_no_finishes_every_live_source_and_lets_its_claim_go() {
        var probe = new RecordingProbe();
        probe.Report(true);
        await using var recorder = NewRecorder(probe: probe);
        var first  = Source(Transcript(Prompt, Reply));
        var second = Source(Tmp.PathTo("second.jsonl"), "a1");
        await recorder.TryRecordAsync(first, CancellationToken.None);
        await recorder.TryRecordAsync(second, CancellationToken.None);

        probe.Forget();
        probe.Report(false);

        await WaitHarness.PollUntilAsync(() => recorder.ActiveKeys.Count == 0
                                            && Claims.Read(first.Key) is RecorderClaimState.None && Claims.Read(second.Key) is RecorderClaimState.None
                                            && List.Load().Count == 0);
        await Assert.That(Transport.DrainCompletes).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>Claimed ahead of a launch that hasn't started: nothing would record it, and the claim would hold back its hooks.</summary>
    [Test]
    public async Task A_definite_no_lets_a_claim_go_ahead_of_its_start() {
        var probe = new RecordingProbe();
        probe.Report(true);
        await using var recorder = NewRecorder(probe: probe);
        var source = Source(Transcript(Prompt));
        await recorder.ClaimAsync(source, CancellationToken.None);

        probe.Forget();
        probe.Report(false);

        await WaitHarness.PollUntilAsync(() => Claims.Read(source.Key) is RecorderClaimState.None);
        recorder.StartForAgent(source);
        await Assert.That(recorder.ActiveKeys).IsEmpty();
    }

    /// <summary>Losing the connection is not an answer, so a source lives through it.</summary>
    [Test]
    public async Task A_connection_going_away_leaves_live_sources_alone() {
        var probe = new RecordingProbe();
        probe.Report(true);
        await using var recorder = NewRecorder(probe: probe);
        var source = Source(Transcript(Prompt, Reply));
        await recorder.TryRecordAsync(source, CancellationToken.None);

        probe.Forget();

        await Assert.That(recorder.ActiveKeys).IsEquivalentTo([source.Key]);
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

    /// <summary>The probe goes on asking, so an answer can land long after the boot gave up on it, and a hard-killed
    /// source's tail shouldn't have to wait for the next boot. One restore comes of it, however many answers follow.</summary>
    [Test]
    public async Task A_first_yes_after_the_boot_restores_once() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = LeftBehind();
        var probe  = new RecordingProbe();
        await using var recorder = NewRecorder(time: time, probe: probe);

        var restore = recorder.RestoreAsync(InScope, CancellationToken.None);
        time.Advance(DaemonRecorder.AnswerWait);
        await restore.WithinDeadlineAsync("the restore");
        await recorder.SettleRestoredAsync(_ => true);

        probe.Report(true);
        await WaitHarness.PollUntilAsync(() => recorder.ActiveKeys.Contains(source.Key) && Transport.Connects == 1);

        probe.Forget();
        probe.Report(true);
        await Assert.That(Connections).IsEqualTo(1);
        await Assert.That(Transport.ConnectAttempts).IsEqualTo(1);
    }

    /// <summary>The boot's settling ran long ago, and nothing else ends a source no agent came back for.</summary>
    [Test]
    public async Task A_late_restore_finishes_a_source_whose_agent_did_not_come_back() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = LeftBehind();
        var probe  = new RecordingProbe();
        await using var recorder = NewRecorder(time: time, probe: probe);

        var restore = recorder.RestoreAsync(InScope, CancellationToken.None);
        time.Advance(DaemonRecorder.AnswerWait);
        await restore.WithinDeadlineAsync("the restore");
        await recorder.SettleRestoredAsync(_ => false);

        probe.Report(true);

        await WaitHarness.PollUntilAsync(() => Transport.Detached.Contains(source.Key) && Claims.Read(source.Key) is RecorderClaimState.None && List.Load().Count == 0);
    }

    [Test]
    public async Task A_late_no_lets_go_of_what_the_boot_left() {
        var time   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = LeftBehind();
        var probe  = new RecordingProbe();
        await using var recorder = NewRecorder(time: time, probe: probe);

        var restore = recorder.RestoreAsync(InScope, CancellationToken.None);
        time.Advance(DaemonRecorder.AnswerWait);
        await restore.WithinDeadlineAsync("the restore");
        await recorder.SettleRestoredAsync(_ => false);

        probe.Report(false);

        await WaitHarness.PollUntilAsync(() => Claims.Read(source.Key) is RecorderClaimState.None && List.Load().Count == 0);
        await Assert.That(MarkedForImport).IsTrue();
        await Assert.That(Connections).IsEqualTo(0);
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
        await recorder.SettleRestoredAsync(_ => false);

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
        await recorder.SettleRestoredAsync(_ => false);

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
