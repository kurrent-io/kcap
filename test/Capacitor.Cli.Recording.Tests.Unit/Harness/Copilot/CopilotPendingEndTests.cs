using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Harness.Copilot;

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
    [Arguments("""{"type":"user.message","data":{"content":"next"},"id":"evt-u","timestamp":3}""")]
    public async Task An_end_is_dropped_when_a_new_run_begins(string line) {
        using var runtime = DummyProcess.StartSleep(30);
        var transcript = Transcript(Turn);
        CopilotPendingEnd.Write(Paths, Session, End, Server, runtime.Pid, transcript, TimeProvider.System);
        await File.AppendAllTextAsync(transcript, line + "\n");

        await Assert.That(Sweep()).IsEqualTo(0);
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

        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow - CopilotPendingEnd.QuietPeriod - TimeSpan.FromMinutes(1));

        await Assert.That(Sweep()).IsEqualTo(1);
        await Assert.That(Spool.HasBacklog(Session)).IsTrue();
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
