using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Harness.Copilot;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

/// <summary>An end a stopped Copilot watcher could not yet prove is held until a later kcap process can.</summary>
public class CopilotPendingEndTests {
    const string Session = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    const string Server  = "https://kcap.example";
    const string Turn    = """{"type":"assistant.message","data":{"content":"hi"},"id":"evt-1","timestamp":1}""";
    const string Down    = """{"type":"session.shutdown","data":{},"id":"evt-2","timestamp":2}""";
    const string End     = """{"session_id":"0f1e2d3c4b5a69788796a5b4c3d2e1f0","reason":"parent_exited"}""";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    WatcherPaths Paths => field ??= WatcherPaths.FromEnvironment(Config.Root);

    HookSpool Spool => field ??= new HookSpool(Config.Root, TimeProvider.System, Server);

    string Transcript(string lines) => Config.CreateFile("events.jsonl", lines + "\n");

    int Sweep() => CopilotPendingEnd.SpoolDue(Paths, Spool, Server, TimeProvider.System);

    bool Pending => CopilotPendingEnd.SessionsWithPending(Paths).Contains(Session);

    /// <summary>A sweep now and one past the 30 s a run mismatch must hold on an unchanged transcript.</summary>
    int SweepUntilSettled() => Sweep() + SweepLater();

    int SweepLater() => CopilotPendingEnd.SpoolDue(Paths, Spool, Server, new FakeTimeProvider(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(31)));

    [Test]
    public async Task An_end_is_spooled_once_its_runtime_has_exited() {
        var runtime    = DummyProcess.StartSleep(30);
        var transcript = Transcript(Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System);

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsTrue();

        runtime.Kill();
        runtime.WaitForExit(TimeSpan.FromSeconds(5));
        runtime.Dispose();

        await Assert.That(Sweep()).IsEqualTo(1);
        await Assert.That(Spool.HasBacklog(Session)).IsTrue();
        await Assert.That(Pending).IsFalse();
        await Assert.That(CopilotSpanClose.Pending(Paths, Server)).Contains(Session);
    }

    [Test]
    public async Task An_end_is_spooled_once_the_transcript_ends_on_shutdown() {
        using var runtime = DummyProcess.StartSleep(30);
        var transcript = Transcript(Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System);
        await File.AppendAllTextAsync(transcript, Down + "\n");

        await Assert.That(Sweep()).IsEqualTo(1);
        await Assert.That(Spool.HasBacklog(Session)).IsTrue();
    }

    /// <summary>A new run is the session going on: its own hooks and watcher own the end.</summary>
    [Test]
    [Arguments("""{"type":"session.resume","data":{},"id":"evt-r","timestamp":3}""")]
    [Arguments("""{"type":"session.start","data":{},"id":"evt-s","timestamp":3}""")]
    public async Task An_end_is_dropped_when_a_new_run_begins(string line) {
        using var runtime = DummyProcess.StartSleep(30);
        var transcript = Transcript(Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System);
        await File.AppendAllTextAsync(transcript, line + "\n");

        await Assert.That(SweepUntilSettled()).IsEqualTo(0);
        await Assert.That(Pending).IsFalse();
        await Assert.That(Spool.HasBacklog(Session)).IsFalse();
    }

    /// <summary>Copilot can append the final turn after its watcher was stopped and before it writes the shutdown line:
    /// that is the run finishing, not a new one.</summary>
    [Test]
    public async Task An_end_waits_through_the_final_turn_for_the_shutdown_line() {
        using var runtime = DummyProcess.StartSleep(30);
        var transcript = Transcript(Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System);
        await File.AppendAllTextAsync(transcript, Turn + "\n");

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsTrue();

        await File.AppendAllTextAsync(transcript, Down + "\n");

        await Assert.That(Sweep()).IsEqualTo(1);
        await Assert.That(Spool.HasBacklog(Session)).IsTrue();
    }

    /// <summary>Every owner may send its end, and the server keeps the first one for a session. The hook's, stamped at
    /// the quit, is already spooled when a stopped watcher's end becomes due, so it goes first.</summary>
    [Test]
    public async Task A_stopped_watchers_end_follows_the_hooks_into_the_server() {
        const string hookEnd = """{"session_id":"0f1e2d3c4b5a69788796a5b4c3d2e1f0","reason":"user_exit"}""";
        Spool.Append(Session, CopilotPendingEnd.Route, hookEnd);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, Transcript(Down), TimeProvider.System);
        await Assert.That(Sweep()).IsEqualTo(1);

        var sent = new List<string>();
        await LifecycleSpoolDrain.RunAsync(Config.Root, Spool, new TranscriptSpool(Config.Root, TimeProvider.System, Server), Session,
            (_, body) => {
                sent.Add(body);
                return Task.FromResult(DrainOutcome.Delivered);
            },
            _ => Task.FromResult(DrainOutcome.Delivered), TimeSpan.FromSeconds(5), TimeProvider.System, CancellationToken.None);

        await Assert.That(sent).IsNotEmpty();
        await Assert.That(sent[0]).IsEqualTo(hookEnd);
    }

    /// <summary>A watcher that never resolved its runtime has no process to outlive, so a transcript gone quiet stands in.</summary>
    [Test]
    public async Task An_end_with_no_runtime_is_spooled_once_the_transcript_goes_quiet() {
        var transcript = Transcript(Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System);

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsTrue();

        var later = new FakeTimeProvider(DateTimeOffset.UtcNow + CopilotPendingEnd.QuietPeriod + TimeSpan.FromMinutes(1));

        await Assert.That(CopilotPendingEnd.SpoolDue(Paths, Spool, Server, later)).IsEqualTo(1);
        await Assert.That(Spool.HasBacklog(Session)).IsTrue();
    }

    /// <summary>The quiet period runs from the stop too: a chat idle long before its watcher was stopped is not over yet.</summary>
    [Test]
    public async Task An_end_with_no_runtime_is_held_when_the_chat_was_idle_before_the_stop() {
        var transcript = Transcript(Turn);
        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow - CopilotPendingEnd.QuietPeriod - TimeSpan.FromMinutes(1));
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System);

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsTrue();
    }

    /// <summary>A start token that could not be read says nothing about the runtime: a live one keeps the end held until
    /// the quiet period, the same as no pid at all.</summary>
    [Test]
    public async Task An_end_whose_runtime_token_was_unreadable_waits_for_a_live_runtime() {
        using var runtime = DummyProcess.StartSleep(30);
        var transcript = Transcript(Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System);

        var record = Path.Combine(Paths.Directory, Session + ".copilot-pending-end.json");
        var node   = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(record))!;
        node["runtime_token"] = null;
        await File.WriteAllTextAsync(record, node.ToJsonString());

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsTrue();

        var later = new FakeTimeProvider(DateTimeOffset.UtcNow + CopilotPendingEnd.QuietPeriod + TimeSpan.FromMinutes(1));
        await Assert.That(CopilotPendingEnd.SpoolDue(Paths, Spool, Server, later)).IsEqualTo(1);
    }

    /// <summary>A pending end is promoted only with the span close it owes; one that cannot be recorded stays pending.</summary>
    [Test, RunOn(OS.Linux | OS.MacOs)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task An_end_whose_span_close_cannot_be_recorded_stays_pending() {
        var transcript = Transcript(Down);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System);
        File.SetUnixFileMode(Paths.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try {
            await Assert.That(Sweep()).IsEqualTo(0);
            await Assert.That(Spool.HasBacklog(Session)).IsFalse();
            await Assert.That(Pending).IsTrue();
        } finally {
            File.SetUnixFileMode(Paths.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A resumed run that shuts down too ends on a shutdown line the stopped run never wrote.</summary>
    [Test]
    public async Task An_end_is_dropped_when_a_new_run_begins_and_shuts_down() {
        using var runtime = DummyProcess.StartSleep(30);
        var transcript = Transcript(Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System);
        await File.AppendAllTextAsync(transcript, """{"type":"session.resume","data":{},"id":"evt-r","timestamp":3}""" + "\n" + Down + "\n");

        await Assert.That(SweepUntilSettled()).IsEqualTo(0);
        await Assert.That(Pending).IsFalse();
    }

    const string StartA  = """{"type":"session.start","data":{},"id":"run-a","timestamp":1}""";
    const string ResumeB = """{"type":"session.resume","data":{},"id":"run-b","timestamp":4}""";

    /// <summary>The stopped run is the one the transcript named when the stop arrived: a run that began before the record
    /// was written is still a new one.</summary>
    [Test]
    public async Task An_end_is_dropped_when_a_new_run_began_before_it_was_recorded() {
        using var runtime = DummyProcess.StartSleep(30);
        var transcript = Transcript(StartA + "\n" + Turn + "\n" + ResumeB);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System, runId: "run-a");

        await Assert.That(SweepUntilSettled()).IsEqualTo(0);
        await Assert.That(Pending).IsFalse();
    }

    /// <summary>VS Code's Restore checkpoint rewrites the transcript in place, shorter than the stop found it. A run resumed
    /// after that is still a new run, wherever its line now sits.</summary>
    [Test]
    public async Task An_end_is_dropped_when_a_run_resumes_after_a_restore_rewrote_the_transcript() {
        using var runtime = DummyProcess.StartSleep(30);
        var transcript = Transcript(StartA + "\n" + Turn + "\n" + Turn + "\n" + Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System, runId: "run-a");
        await File.WriteAllTextAsync(transcript, StartA + "\n" + ResumeB + "\n");

        await Assert.That(SweepUntilSettled()).IsEqualTo(0);
        await Assert.That(Pending).IsFalse();
    }

    /// <summary>A rewrite that took the stopped run's own line away leaves its end ambiguous: a premature first end cannot be
    /// undone, so it is dropped.</summary>
    [Test]
    public async Task An_end_is_dropped_when_a_rewrite_removed_its_run() {
        var transcript = Transcript(StartA + "\n" + Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, runId: "run-a");
        await File.WriteAllTextAsync(transcript, Turn + "\n" + Down + "\n");

        await Assert.That(SweepUntilSettled()).IsEqualTo(0);
        await Assert.That(Pending).IsFalse();
        await Assert.That(Spool.HasBacklog(Session)).IsFalse();
    }

    /// <summary>A read can catch VS Code's in-place rewrite with the file empty. The run reappearing once the rewrite
    /// completes clears the mismatch, and the end goes when the run shuts down.</summary>
    [Test]
    public async Task An_end_survives_a_read_that_caught_a_rewrite_half_done() {
        var transcript = Transcript(StartA + "\n" + Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, runId: "run-a");
        await File.WriteAllTextAsync(transcript, "");

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsTrue();

        await File.WriteAllTextAsync(transcript, StartA + "\n" + Down + "\n");

        await Assert.That(SweepLater()).IsEqualTo(1);
    }

    /// <summary>The run must stay gone on an unchanged file across two passes 30 s apart; one pass alone proves nothing.</summary>
    [Test]
    public async Task A_run_found_gone_is_dropped_only_once_the_absence_holds() {
        var transcript = Transcript(StartA + "\n" + Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, runId: "run-a");
        await File.WriteAllTextAsync(transcript, Turn + "\n");

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsTrue();

        await Assert.That(SweepLater()).IsEqualTo(0);
        await Assert.That(Pending).IsFalse();
    }

    /// <summary>A known run waits while its transcript cannot be read: a gone runtime alone does not promote an end that
    /// may belong to a run the transcript would show superseded.</summary>
    [Test]
    public async Task An_unreadable_transcript_keeps_a_known_runs_end_waiting() {
        var runtime    = DummyProcess.StartSleep(30);
        var transcript = Transcript(StartA + "\n" + Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System, runId: "run-a");
        runtime.Kill();
        runtime.WaitForExit(TimeSpan.FromSeconds(5));
        runtime.Dispose();
        File.Delete(transcript);

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsTrue();
    }

    /// <summary>The same run, still the latest, keeps its end through a rewrite and sends it once it shuts down.</summary>
    [Test]
    public async Task An_end_of_the_latest_run_is_kept_through_a_rewrite() {
        using var runtime = DummyProcess.StartSleep(30);
        var transcript = Transcript(StartA + "\n" + Turn + "\n" + Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System, runId: "run-a");
        await File.WriteAllTextAsync(transcript, StartA + "\n" + Down + "\n");

        await Assert.That(Sweep()).IsEqualTo(1);
    }

    /// <summary>A watcher started since this one stopped records the session and sends its end, however quiet it is.</summary>
    [Test]
    public async Task An_end_is_dropped_once_another_watcher_records_the_session() {
        var transcript = Transcript(Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, watcherPid: 999999);
        using var successor = DummyProcess.StartSleep(30);
        Directory.CreateDirectory(Paths.Directory);
        await File.WriteAllTextAsync(Paths.PidFile(Session), $"{successor.Pid}\n{PidIdentity.Capture(successor.Pid)}");

        var later = new FakeTimeProvider(DateTimeOffset.UtcNow + CopilotPendingEnd.QuietPeriod + TimeSpan.FromMinutes(1));
        await Assert.That(CopilotPendingEnd.SpoolDue(Paths, Spool, Server, later)).IsEqualTo(0);
        await Assert.That(Pending).IsFalse();
        await Assert.That(Spool.HasBacklog(Session)).IsFalse();
    }

    /// <summary>The watcher that wrote the record is not another one.</summary>
    [Test]
    public async Task An_end_is_kept_while_its_own_watcher_is_still_stopping() {
        var transcript = Transcript(Turn);
        using var writer = DummyProcess.StartSleep(30);
        Directory.CreateDirectory(Paths.Directory);
        await File.WriteAllTextAsync(Paths.PidFile(Session), $"{writer.Pid}\n{PidIdentity.Capture(writer.Pid)}");
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, watcherPid: writer.Pid);

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsTrue();
    }

    [Test]
    public async Task A_torn_record_is_dropped() {
        Directory.CreateDirectory(Paths.Directory);
        await File.WriteAllTextAsync(Path.Combine(Paths.Directory, Session + ".copilot-pending-end.json"), """{"server":"https://kcap""");

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsFalse();
    }

    /// <summary>A drain holds a Copilot end only behind a watcher it can prove is still the one that was running, and only
    /// for a bounded time after the end: a reused pid, an unreadable pid file or a wedged watcher must not strand it.</summary>
    [Test]
    [Arguments("verified", 0, true)]
    [Arguments("verified", 5, false)]
    [Arguments("pid-only", 0, false)]
    [Arguments("unreadable", 0, false)]
    [Arguments("none", 0, false)]
    public async Task A_drain_holds_an_end_only_behind_a_verified_watcher_for_a_bounded_time(string watcher, int minutesSinceEnd, bool held) {
        using var process = DummyProcess.StartSleep(30);
        Directory.CreateDirectory(Paths.Directory);
        var pidFile = Paths.PidFile(Session);

        switch (watcher) {
            case "verified":   await File.WriteAllTextAsync(pidFile, $"{process.Pid}\n{PidIdentity.Capture(process.Pid)}"); break;
            case "pid-only":   await File.WriteAllTextAsync(pidFile, $"{process.Pid}"); break;
            case "unreadable": Directory.CreateDirectory(pidFile); break;
        }

        var now = DateTimeOffset.UtcNow;
        var end = new System.Text.Json.Nodes.JsonObject { ["session_id"] = Session, ["ended_at"] = (now - TimeSpan.FromMinutes(minutesSinceEnd)).ToString("O") }.ToJsonString();

        await Assert.That(CopilotPendingEnd.HeldForWatcher(Paths, CopilotPendingEnd.Route, end, new FakeTimeProvider(now))).IsEqualTo(held);
    }

    static string? RunIdOnDisk(WatcherPaths paths) {
        var file = Path.Combine(paths.Directory, Session + ".copilot-pending-end.json");
        return File.Exists(file) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))?["run_id"]?.GetValue<string>() : null;
    }

    static System.Text.Json.Nodes.JsonObject Snapshot(WatcherPaths paths) =>
        (System.Text.Json.Nodes.JsonObject) System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(paths.Directory, Session + ".copilot-pending-end.json")))!;

    /// <summary>A sweep that read run A's record holds the session's lock until it is done with it, so a new run's hook and
    /// watcher writing run B's meanwhile land after the sweep's own write of A, never under it. The test holds the lock and
    /// saves A's snapshot as the sweep would.</summary>
    [Test]
    public async Task A_sweep_saving_what_it_read_cannot_overwrite_a_new_runs_end() {
        var transcript = Transcript(StartA + "\n" + Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, runId: "run-a");
        var readByTheSweep = Snapshot(Paths);

        Task newRun;
        using (CopilotPendingEndLock.TryAcquire(Paths, Session)) {
            newRun = Task.Run(() => {
                CopilotPendingEnd.Discard(Paths, Session);
                CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, runId: "run-b");
            });
            await Task.Delay(300);

            readByTheSweep["mismatch"] = new System.Text.Json.Nodes.JsonObject { ["since"] = DateTimeOffset.UtcNow.ToString("O"), ["shape"] = "0:0" };
            CopilotPendingEnd.Save(Paths, Session, readByTheSweep);
        }

        await newRun;

        await Assert.That(RunIdOnDisk(Paths)).IsEqualTo("run-b");
    }

    /// <summary>A hook's discard waits out a sweep holding the record, so the sweep cannot bring back what the hook
    /// discarded.</summary>
    [Test]
    public async Task A_sweep_saving_what_it_read_cannot_revive_an_end_a_hook_discarded() {
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, Transcript(StartA + "\n" + Turn), TimeProvider.System, runId: "run-a");
        var readByTheSweep = Snapshot(Paths);

        Task hook;
        using (CopilotPendingEndLock.TryAcquire(Paths, Session)) {
            hook = Task.Run(() => CopilotPendingEnd.Discard(Paths, Session));
            await Task.Delay(300);

            CopilotPendingEnd.Save(Paths, Session, readByTheSweep);
        }

        await hook;

        await Assert.That(Pending).IsFalse();
    }

    /// <summary>A watcher that posted its own end discards only the record it wrote, not a later watcher's.</summary>
    [Test]
    public async Task A_watchers_discard_leaves_a_later_watchers_end() {
        var transcript = Transcript(StartA + "\n" + Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, runId: "run-a", watcherPid: 100);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, runId: "run-b", watcherPid: 200);

        CopilotPendingEnd.Discard(Paths, Session, ownedBy: 100);

        await Assert.That(RunIdOnDisk(Paths)).IsEqualTo("run-b");
    }

    /// <summary>A sweep reads the transcript with no lock held, so however long that takes, a watcher writing its end meanwhile
    /// is not refused; the sweep then finds the record changed and leaves it. A named pipe stands in for a transcript whose
    /// read does not finish until the test lets it.</summary>
    [Test, RunOn(OS.Linux | OS.MacOs)]
    public async Task A_long_transcript_read_does_not_hold_back_a_watchers_write() {
        var transcript = Config.PathTo("events.fifo");
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [transcript])!) await mkfifo.WaitForExitAsync();
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, runId: "run-a");

        var sweep = Task.Run(Sweep);
        await Task.Delay(300);

        var clock   = System.Diagnostics.Stopwatch.StartNew();
        var written = CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, runId: "run-b");
        clock.Stop();

        await using (new FileStream(transcript, FileMode.Open, FileAccess.Write)) { }
        await sweep;

        await Assert.That(written).IsTrue();
        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
        await Assert.That(RunIdOnDisk(Paths)).IsEqualTo("run-b");
    }

    /// <summary>A hook sweeps inside Copilot's shared shutdown budget, so it waits for no record another writer holds.</summary>
    [Test]
    public async Task A_hook_sweep_skips_a_record_another_writer_holds() {
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, Transcript(StartA + "\n" + Down), TimeProvider.System, runId: "run-a");

        using (CopilotPendingEndLock.TryAcquire(Paths, Session)) {
            var clock   = System.Diagnostics.Stopwatch.StartNew();
            var spooled = CopilotPendingEnd.SpoolDue(Paths, Spool, Server, TimeProvider.System, lockWait: TimeSpan.Zero, budget: TimeSpan.FromMilliseconds(250));
            clock.Stop();

            await Assert.That(spooled).IsEqualTo(0);
            await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
        }

        await Assert.That(Sweep()).IsEqualTo(1);
    }

    /// <summary>A watcher retries its end through its stop window rather than lose it to a writer holding the record.</summary>
    [Test]
    public async Task A_watchers_write_is_retried_past_a_held_record() {
        var transcript = Transcript(StartA + "\n" + Turn);
        var held       = CopilotPendingEndLock.TryAcquire(Paths, Session)!;
        var release    = Task.Run(async () => {
            await Task.Delay(TimeSpan.FromSeconds(1));
            held.Dispose();
        });

        var written = await CopilotPendingEnd.WriteWithRetryAsync(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, TimeSpan.FromSeconds(10), "run-a",
            attemptWait: TimeSpan.FromMilliseconds(100));
        await release;

        await Assert.That(written).IsTrue();
        await Assert.That(RunIdOnDisk(Paths)).IsEqualTo("run-a");
    }

    [Test]
    public async Task An_end_of_a_disabled_session_is_dropped() {
        var transcript = Transcript(Down);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System);
        DisabledSessions.Mark(Session, Config.Root);

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsFalse();
    }

    [Test]
    public async Task An_end_for_another_server_is_left_to_its_drain() {
        var transcript = Transcript(Down);
        CopilotPendingEnd.Write(Paths, Session, End, "https://other.example", runtimePid: null, transcript, TimeProvider.System);

        await Assert.That(Sweep()).IsEqualTo(0);
        await Assert.That(Pending).IsTrue();
    }
}
