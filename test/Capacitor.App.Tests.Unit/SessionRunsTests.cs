using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// The per-workspace tracker as a pure state machine over projection results: signals first,
/// then envelopes. An end that says how is final; only an end with no outcome is revised, by one
/// that says how or by a wait showing the run never ended.
public class SessionRunsTests {
    static readonly DateTimeOffset T0 = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    static FakeTimeProvider Clock() => new(T0);

    static ChatProjectionResult Signals(params RunSignal[] signals) => new([], [], signals);

    static ChatProjectionResult Result(string callId, bool isError = false, DateTimeOffset? at = null) =>
        new([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: callId, ToolIsError: isError, TimestampIso: at?.ToString("O"))], [], []);

    static ChatProjectionResult Mixed(IReadOnlyList<AcpEventEnvelope> envelopes, params RunSignal[] signals) => new(envelopes, [], signals);

    static RunSignal.Started Started(string callId, DateTimeOffset? at = null, string name = "Explore") =>
        new(callId, name, "Map the UI", at ?? T0);

    static RunSignal.Detached Detached(string callId, string agentId) => new(callId, agentId);

    static RunSignal.Finished Finished(string? callId, string? agentId, RunOutcome? outcome = RunOutcome.Done, DateTimeOffset? at = null) =>
        new(callId, agentId, outcome, at ?? T0.AddMinutes(2));

    static RunSignal.Waiting Waiting(string? callId, string? agentId, DateTimeOffset at) => new(callId, agentId, at);

    static RunSignal.Resumed Resumed(string callId, string agentId, DateTimeOffset at) => new(callId, agentId, at);

    static RunSignal.Stopped Stop(string agentId, DateTimeOffset at) => new(agentId, at);

    static RunRow Only(SessionRuns s) => s.Rows.Single();

    static RunSignal.Identified Identified(string? callId, string agentId) => new(callId, agentId);

    static RunSignal.Started Shell(string callId, DateTimeOffset? at = null) =>
        new(callId, "Run the full suite", "dotnet test", at ?? T0, RunKind.Shell, Provisional: true);

    [Test]
    public async Task A_foreground_shell_call_leaves_no_row() {
        var s = new SessionRuns(Clock());
        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Shell("c1")));
        await Assert.That(s.Rows).IsEmpty();
        s.Apply(Result("c1"));
        await Assert.That(s.Rows).IsEmpty();
        // The pending entry went with the result: a stray Detached later makes no row.
        s.Apply(Signals(Detached("c1", "b1")));
        await Assert.That(s.Rows).IsEmpty();
        await Assert.That(changes).IsEqualTo(0);
    }

    [Test]
    public async Task A_batch_that_starts_and_finishes_a_command_never_reports_it_running() {
        var s = new SessionRuns(Clock());
        var seen = new List<int>();
        s.Changed += () => seen.Add(s.RunningCommandCount);
        s.ApplyAll([
            Signals(Shell("c1")),
            Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "b1")),
            Signals(Finished("c1", "b1")),
        ]);
        await Assert.That(seen).IsEquivalentTo([0]);
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
    }

    [Test]
    public async Task A_background_shell_runs_from_its_detach_until_its_notification() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "b1")));
        var row = Only(s);
        await Assert.That(row.Kind).IsEqualTo(RunKind.Shell);
        await Assert.That(row.IsShell).IsTrue();
        await Assert.That(row.Name).IsEqualTo("Run the full suite");
        await Assert.That(row.Description).IsEqualTo("dotnet test");
        await Assert.That(row.IsBackground).IsTrue();
        await Assert.That(row.StateText).IsEqualTo("running in background · 0s");
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", "b1", RunOutcome.Stopped, at: T0.AddSeconds(30))));
        await Assert.That(row.State).IsEqualTo(RunState.Stopped);
        await Assert.That(row.StateText).IsEqualTo("stopped · 30s");
    }

    [Test]
    public async Task A_timed_out_shell_is_dated_from_its_call() {
        var clock = Clock();
        var s = new SessionRuns(clock);
        s.Apply(Signals(Shell("c1", at: T0)));
        clock.Advance(TimeSpan.FromMinutes(2));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "b1")));
        await Assert.That(Only(s).StartedAt).IsEqualTo(T0);
        await Assert.That(Only(s).StateText).IsEqualTo("running in background · 2m 00s");
    }

    [Test]
    public async Task A_detach_for_an_unseen_call_makes_no_row() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Detached("c9", "b9"), Finished("c9", "b9")));
        await Assert.That(s.Rows).IsEmpty();
    }

    [Test]
    public async Task A_shell_stopped_by_its_task_id_alone_ends() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Signals(Detached("c1", "b1")));
        s.Apply(Signals(Finished(null, "b1", RunOutcome.Stopped)));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
    }

    [Test]
    public async Task Session_over_presents_a_running_shell_as_stopped() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Signals(Detached("c1", "b1")));
        s.SessionOver = true;
        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
        await Assert.That(Only(s).StateText).IsEqualTo("stopped");
    }

    [Test]
    public async Task A_repeated_provisional_start_for_a_live_row_changes_nothing() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Signals(Detached("c1", "b1")));
        s.Apply(Signals(Shell("c1")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).IsRunning).IsTrue();
    }

    [Test]
    public async Task Clear_drops_pending_shell_calls() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Clear();
        s.Apply(Signals(Detached("c1", "b1")));
        await Assert.That(s.Rows).IsEmpty();
    }

    [Test]
    public async Task An_agent_start_is_an_agent_row() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        await Assert.That(Only(s).Kind).IsEqualTo(RunKind.Agent);
        await Assert.That(Only(s).IsShell).IsFalse();
    }

    [Test]
    public async Task A_foreground_start_and_its_result_make_one_done_row() {
        var s = new SessionRuns(Clock());
        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Started("c1")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(s.RunningCount).IsEqualTo(1);
        await Assert.That(Only(s).Name).IsEqualTo("Explore");
        await Assert.That(Only(s).Description).IsEqualTo("Map the UI");
        await Assert.That(Only(s).IsBackground).IsFalse();
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Result("c1", at: T0.AddSeconds(48)));
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(s).StateText).IsEqualTo("48s");
        await Assert.That(changes).IsEqualTo(2);
    }

    [Test]
    public async Task A_background_launch_stays_running_past_its_result_until_the_notification() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        await Assert.That(Only(s).IsBackground).IsTrue();
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
        await Assert.That(Only(s).StateText).IsEqualTo("running in background · 0s");
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", "a1", at: T0.AddMinutes(2).AddSeconds(41))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(s).StateText).IsEqualTo("2m 41s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_failed_result_and_a_failed_notification_read_as_failed() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        s.Apply(Result("c1", isError: true, at: T0.AddSeconds(48)));
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Failed);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("failed · 48s");

        s.Apply(Signals(Detached("c2", "a2")));
        s.Apply(Signals(Finished("c2", "a2", RunOutcome.Failed, at: T0.AddSeconds(62))));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Failed);
        await Assert.That(s.Rows[1].StateText).IsEqualTo("failed · 1m 02s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_start_record_row_is_named_and_ended_by_its_agent() {
        var s     = new SessionRuns(Clock());
        var start = Signals([.. TranscriptChat.SubagentStart("a1", "explorer", T0, rules: null)]);
        s.Apply(start);
        s.Apply(start);

        await Assert.That(Only(s).AgentId).IsEqualTo("a1");
        await Assert.That(Only(s).CanOpen).IsTrue();
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished(null, "a1", outcome: null, at: T0.AddSeconds(62))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_second_start_for_the_same_agent_is_its_own_row() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals([.. TranscriptChat.SubagentStart("a1", "explorer", T0, rules: null)]));
        s.Apply(Signals(Finished(null, "a1", outcome: null, at: T0.AddMinutes(1))));
        s.Apply(Signals([.. TranscriptChat.SubagentStart("a1", "explorer", T0.AddMinutes(5), rules: null)]));

        await Assert.That(s.Rows.Count).IsEqualTo(2);
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished(null, "a1", outcome: null, at: T0.AddMinutes(6))));
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    /// A lane that cannot read a subagent's transcript must not offer a row that opens nothing.
    [Test]
    public async Task A_row_offers_to_open_only_where_the_lane_can_open_it() {
        var s = new SessionRuns(Clock()) { OpensSubagents = false };
        s.Apply(Signals([.. TranscriptChat.SubagentStart("a1", "explorer", T0, rules: null)]));
        await Assert.That(Only(s).CanOpen).IsFalse();

        s.OpensSubagents = true;
        await Assert.That(Only(s).CanOpen).IsTrue();
    }

    /// <summary>An ended session (an import among them) whose records never tied the call to an agent keeps
    /// the row shut and says why, even when a start record named an agent: the row had ended before it
    /// arrived, so pairing them would be a guess.</summary>
    [Test]
    public async Task An_ended_row_with_no_recorded_agent_says_why_it_does_not_open() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Result("c1", at: T0.AddMinutes(1)));
        s.Apply(Signals([.. TranscriptChat.SubagentStart("a1", "Explore", T0.AddMinutes(5), ClaudeChatRules.Instance)]));
        s.SessionOver = true;

        await Assert.That(Only(s).CanOpen).IsFalse();
        await Assert.That(Only(s).StateWord).IsEqualTo("done");
        await Assert.That(Only(s).UnopenableReason).Contains("no record of which subagent transcript this call started");
    }

    /// <summary>The call's own record names its agent, which opens the row after the session ended too.</summary>
    [Test]
    public async Task An_ended_row_named_by_its_call_opens_and_gives_no_reason() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Identified("c1", "a1")));
        s.SessionOver = true;

        await Assert.That(Only(s).CanOpen).IsTrue();
        await Assert.That(Only(s).UnopenableReason).IsNull();
    }

    [Test]
    public async Task A_running_row_waiting_for_its_agent_says_it_will_open() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));

        await Assert.That(Only(s).UnopenableReason).StartsWith("Opens once");

        s.Apply(Signals(Identified("c1", "a1")));
        await Assert.That(Only(s).UnopenableReason).IsNull();
    }

    [Test]
    public async Task A_lane_that_cannot_read_subagents_says_so_and_a_command_gives_no_reason() {
        var s = new SessionRuns(Clock()) { OpensSubagents = false };
        s.Apply(Signals(Started("c1"), Identified("c1", "a1"), Shell("c2")));
        s.Apply(Signals(Detached("c2", "b2")));

        await Assert.That(s.Rows[0].UnopenableReason).IsEqualTo("Subagent transcripts can't be opened for this session.");
        await Assert.That(s.Rows[1].IsShell).IsTrue();
        await Assert.That(s.Rows[1].UnopenableReason).IsNull();
    }

    [Test]
    public async Task A_background_row_is_stopped_by_its_agent_id_alone() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("c1", "a1")));
        s.Apply(Signals(Finished(null, "a1", RunOutcome.Stopped, at: T0.AddSeconds(62))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
        await Assert.That(Only(s).StateText).IsEqualTo("stopped · 1m 02s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    /// The server's stop lands ahead of the notification, which alone says how the run went.
    [Test]
    public async Task A_bare_stop_ends_a_background_row_as_done_until_a_notification_says_how() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Stop("a1", T0.AddSeconds(62))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(s).StateText).IsEqualTo("1m 02s");
        await Assert.That(s.RunningCount).IsEqualTo(0);

        s.Apply(Signals(Finished("c1", "a1", RunOutcome.Failed, at: T0.AddSeconds(90))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Failed);
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddSeconds(62));
        await Assert.That(Only(s).StateText).IsEqualTo("failed · 1m 02s");
    }

    /// The server dates a stop when it heard it: an import or a spooled hook lands it long after
    /// the run ended, and the notification's own time is the truer end.
    [Test]
    public async Task A_late_stamped_bare_stop_yields_to_the_earlier_end_that_says_how() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Stop("a1", T0.AddDays(3))));
        s.Apply(Signals(Finished("c1", "a1", RunOutcome.Done, at: T0.AddSeconds(90))));
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddSeconds(90));
        await Assert.That(Only(s).StateText).IsEqualTo("1m 30s");
    }

    /// A background agent that stops with work of its own still running has paused, not ended: the
    /// stop record of each pause leaves it running, and only the transcript's own end finishes it.
    [Test]
    public async Task An_agent_waiting_on_its_own_work_runs_through_its_pauses_until_its_end() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Waiting("c1", "a1", T0.AddMinutes(10)), Stop("a1", T0.AddMinutes(10))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Stop("a1", T0.AddMinutes(20)), Waiting(null, "a1", T0.AddMinutes(20))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished(null, "a1", RunOutcome.Done, at: T0.AddMinutes(30))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddMinutes(30));
    }

    /// The server can record a pause's stop ahead of the notification saying the agent waits, and the wait
    /// takes that end back. An end the transcript reported stays, even one cut off before its status.
    [Test]
    public async Task A_wait_takes_back_a_stop_that_landed_first_but_never_an_end_the_transcript_reported() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1"), Stop("a1", T0.AddMinutes(10))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);

        s.Apply(Signals(Waiting("c1", "a1", T0.AddMinutes(10))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
        await Assert.That(Only(s).EndedAt).IsNull();

        foreach (var outcome in new RunOutcome?[] { null, RunOutcome.Failed }) {
            var reported = new SessionRuns(Clock());
            reported.Apply(Signals(Started("c1"), Detached("c1", "a1"), Finished("c1", "a1", outcome, at: T0.AddMinutes(5))));
            reported.Apply(Signals(Waiting(null, "a1", T0.AddMinutes(6))));
            await Assert.That(Only(reported).IsEnded).IsTrue();
            await Assert.That(Only(reported).EndedAt).IsEqualTo(T0.AddMinutes(5));
        }
    }

    /// A stop names no run and a Claude agent stops at every pause, so once the transcript reports on an
    /// agent, its stops end nothing: a late one from an earlier run must not end the run now going.
    [Test]
    public async Task Once_the_transcript_reports_on_an_agent_its_stops_end_nothing() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1"), Finished("c1", "a1", RunOutcome.Done, at: T0.AddMinutes(2))));
        s.Apply(Signals(Resumed("m1", "a1", T0.AddMinutes(5)), Stop("a1", T0.AddMinutes(6))));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Running);

        s.Apply(Signals(Finished(null, "a1", outcome: null, at: T0.AddMinutes(20))));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Done);
        await Assert.That(s.Rows[1].EndedAt).IsEqualTo(T0.AddMinutes(20));
    }

    /// A message that starts a stopped subagent again is a second run of it: a row of its own under the
    /// first run's name, owning the agent id, which the message's own result does not end.
    [Test]
    public async Task A_resumed_agent_is_a_second_running_row_under_the_first_runs_name() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Finished(null, "a1", RunOutcome.Done, at: T0.AddMinutes(2))));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "m1")], Resumed("m1", "a1", T0.AddMinutes(5))));
        s.Apply(Signals(Resumed("m1", "a1", T0.AddMinutes(5))));

        await Assert.That(s.Rows).Count().IsEqualTo(2);
        var second = s.Rows[1];
        await Assert.That(second.State).IsEqualTo(RunState.Running);
        await Assert.That(second.IsBackground).IsTrue();
        await Assert.That(second.Name).IsEqualTo("Explore");
        await Assert.That(second.Description).IsEqualTo("Map the UI");
        await Assert.That(second.AgentId).IsEqualTo("a1");
        await Assert.That(second.StartedAt).IsEqualTo(T0.AddMinutes(5));
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished(null, "a1", RunOutcome.Done, at: T0.AddMinutes(20))));
        await Assert.That(second.State).IsEqualTo(RunState.Done);
        await Assert.That(s.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(2));
    }

    /// The run a resume follows can still read running, its notification landing after the resume though
    /// dated before it, by call or by agent id alone and cut off before its status or not. The resume ends
    /// it without an outcome, and the notification dates it there rather than ending the new run. An
    /// agent with no row adds none.
    [Test]
    [Arguments("c1", RunOutcome.Failed)]
    [Arguments(null, RunOutcome.Failed)]
    [Arguments(null, null)]
    public async Task A_resume_ends_the_run_before_it_and_that_runs_late_notification_lands_there(string? callId, RunOutcome? outcome) {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Resumed("m1", "a1", T0.AddMinutes(5)), Resumed("m2", "zz", T0.AddMinutes(5))));
        await Assert.That(s.Rows).Count().IsEqualTo(2);
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Done);
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Running);

        s.Apply(Signals(Finished(callId, "a1", outcome, at: T0.AddMinutes(4))));
        await Assert.That(s.Rows[0].State).IsEqualTo(outcome is null ? RunState.Done : RunState.Failed);
        await Assert.That(s.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(4));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Running);
    }

    /// Once an agent has a second run, a stop could be either run's, a pause's or a repeat, and the server
    /// dates it when it heard it: it ends neither, and the late notification still dates the first.
    [Test]
    public async Task A_stop_for_an_agent_with_more_than_one_run_ends_none_of_them() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Resumed("m1", "a1", T0.AddMinutes(5)), Stop("a1", T0.AddMinutes(6)), Stop("a1", T0.AddMinutes(6))));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Running);
        await Assert.That(s.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(5));

        s.Apply(Signals(Finished("c1", "a1", RunOutcome.Done, at: T0.AddMinutes(4))));
        await Assert.That(s.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(4));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(1);
    }

    /// A stop that ended an earlier run stays its end when a later run of the agent is the first the
    /// transcript reports on.
    [Test]
    public async Task A_later_runs_report_leaves_an_earlier_run_ended_by_its_stop() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1"), Stop("a1", T0.AddMinutes(2))));
        s.Apply(Signals(Started("c2", T0.AddMinutes(5)), Detached("c2", "a1")));
        s.Apply(Signals(Finished("c2", "a1", RunOutcome.Done, at: T0.AddMinutes(6))));

        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Done);
        await Assert.That(s.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(2));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Done);
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    /// Claude writes nothing more for an agent paused when its process goes, and its stops count for
    /// nothing once it has reported, so the restart is the only thing left to end the row.
    [Test]
    public async Task A_restart_stops_an_agent_paused_on_its_own_work() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1"), Waiting("c1", "a1", T0.AddMinutes(10)), Stop("a1", T0.AddMinutes(10))));

        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(30))));

        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddMinutes(30));
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_restart_stops_a_background_command() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Signals(Detached("c1", "b1")));

        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(30))));

        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
        await Assert.That(s.RunningCommandCount).IsEqualTo(0);
    }

    /// The server hears a resume from a hook, which can spool past lines the new process wrote.
    [Test]
    public async Task A_restart_heard_late_leaves_a_run_started_after_it() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1", T0.AddMinutes(30)), Detached("c1", "a1")));

        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(10))));

        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
    }

    /// The restart only presumes how a run went: a report that still lands knows better.
    [Test]
    [Arguments(RunOutcome.Done, RunState.Done)]
    [Arguments(RunOutcome.Failed, RunState.Failed)]
    public async Task A_report_after_a_restart_says_how_the_run_ended(RunOutcome outcome, RunState state) {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(30))));

        s.Apply(Signals(Finished(null, "a1", outcome, at: T0.AddMinutes(20))));

        await Assert.That(Only(s).State).IsEqualTo(state);
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddMinutes(20));
    }

    /// An end that does not say how adds nothing to the restart's own reading of the run.
    [Test]
    public async Task A_message_reviving_a_stopped_agent_leaves_its_cut_off_run_stopped() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(30))));

        s.Apply(Signals(Resumed("m1", "a1", T0.AddMinutes(40))));

        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Stopped);
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Running);
    }

    /// A notification can be written after the line that follows it, so a wait from before the restart
    /// can land after it, and must not bring back what a stop or the restart ended.
    [Test]
    public async Task A_wait_landing_after_a_restart_brings_nothing_back() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1"), Stop("a1", T0.AddMinutes(10))));
        s.Apply(Signals(Started("c2"), Detached("c2", "a2")));
        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(30))));

        s.Apply(Signals(Waiting(null, "a1", T0.AddMinutes(10)), Waiting(null, "a2", T0.AddMinutes(10))));

        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    /// A restart can be dated late, and an in-process resume may leave a run going: a wait dated past
    /// the restart can only come from a run that outlived it, however it had ended here.
    [Test]
    public async Task A_wait_dated_after_a_restart_shows_the_run_outlived_it() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1"), Stop("a1", T0.AddMinutes(10))));
        s.Apply(Signals(Started("c2"), Detached("c2", "a2")));
        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(30))));

        s.Apply(Signals(Waiting(null, "a1", T0.AddMinutes(40)), Waiting(null, "a2", T0.AddMinutes(40))));

        await Assert.That(s.RunningCount).IsEqualTo(2);
        await Assert.That(s.Rows[0].EndedAt).IsNull();
        await Assert.That(s.Rows[1].EndedAt).IsNull();
    }

    /// Spooled resumes and late notifications land in any order, so with no report of its own a run
    /// reads by its latest wait against the latest restart alone, however the two were heard.
    [Test]
    [Arguments(20, false, false)]
    [Arguments(40, false, false)]
    [Arguments(60, false, true)]
    [Arguments(20, true, false)]
    [Arguments(40, true, false)]
    [Arguments(60, true, true)]
    public async Task A_run_reads_by_its_latest_wait_and_the_latest_restart_in_any_order(int waitedAt, bool stoppedFirst, bool running) {
        RunSignal[] signals = [new RunSignal.Restarted(T0.AddMinutes(30)), new RunSignal.Restarted(T0.AddMinutes(50)), Waiting(null, "a1", T0.AddMinutes(waitedAt))];
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];

        foreach (var order in orders) {
            var s = new SessionRuns(Clock());
            s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
            if (stoppedFirst) s.Apply(Signals(Stop("a1", T0.AddMinutes(10))));
            foreach (var index in order) s.Apply(Signals(signals[index]));

            await Assert.That(Only(s).IsRunning).IsEqualTo(running).Because(string.Join(",", order));
        }
    }

    /// The server's stream can carry a pre-restart line behind the restart, and it reads as if it had
    /// landed first: the run is cut off, and can still be named and heard from.
    [Test]
    public async Task A_launch_landing_behind_a_restart_is_cut_off_and_can_still_report() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(30))));

        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);

        s.Apply(Signals(Waiting(null, "a1", T0.AddMinutes(40))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
    }

    [Test]
    public async Task A_late_wait_cannot_lower_the_proof_a_newer_one_gave() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Waiting(null, "a1", T0.AddMinutes(60)), Waiting(null, "a1", T0.AddMinutes(10))));

        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(50))));

        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
    }

    [Test]
    public async Task A_line_landing_behind_two_restarts_reads_the_latest_whichever_came_first() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(50)), new RunSignal.Restarted(T0.AddMinutes(30))));

        s.Apply(Signals(Started("c1", T0.AddMinutes(40))));

        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
    }

    [Test]
    public async Task Clear_forgets_the_restart_with_the_rows_it_read() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(30))));

        s.Clear();
        s.Apply(Signals(Started("c1")));

        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
    }

    /// Only the restart's presumption can be taken back: an end that says how is still final.
    [Test]
    public async Task A_wait_after_a_reported_end_brings_nothing_back_past_a_restart() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(new RunSignal.Restarted(T0.AddMinutes(30))));
        s.Apply(Signals(Finished(null, "a1", RunOutcome.Failed, at: T0.AddMinutes(35))));

        s.Apply(Signals(Waiting(null, "a1", T0.AddMinutes(40))));

        await Assert.That(Only(s).State).IsEqualTo(RunState.Failed);
    }

    /// The records Claude writes for an agent caught paused by a restart, end to end through the chat's
    /// projection. A resume can leave the hook record, or only the report of what the old process left
    /// running, which need not name the paused agent.
    [Test]
    [Arguments("""{"type":"attachment","timestamp":"2026-09-17T11:39:00Z","attachment":{"type":"hook_success","hookName":"SessionStart:resume"}}""")]
    [Arguments("""{"type":"user","timestamp":"2026-09-17T11:39:00Z","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>b7k2</task-id>\n<task-id>__orphan_summary__:shell</task-id>\n<status>stopped</status>\n<summary>1 background shell command tasks didn't finish before the previous session ended. First 20 task ids: b7k2.</summary>\n<note>No completion record was found for them in the previous session. They have been marked stopped.</note>\n</task-notification>"}}""")]
    public async Task A_transcript_with_a_paused_agent_stops_it_at_the_restart(string restart) {
        var chat = TranscriptChat.For("claude")!;
        var context = chat.CreateContext("a1", null);
        var s = new SessionRuns(Clock());
        var number = 0;
        foreach (var line in new[] {
            """{"type":"assistant","timestamp":"2026-09-17T10:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Fix it","subagent_type":"general-purpose"}}]}}""",
            """{"type":"user","timestamp":"2026-09-17T10:00:01Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":"launched"}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a151b2d5ee7fd3108"}}""",
            """{"type":"assistant","timestamp":"2026-09-17T10:01:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_B","name":"Bash","input":{"command":"sleep 600","description":"Wait","run_in_background":true}}]}}""",
            """{"type":"user","timestamp":"2026-09-17T10:01:01Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_B","content":"Command running in background with ID: b7k2."}]},"toolUseResult":{"backgroundTaskId":"b7k2"}}""",
            """{"type":"user","timestamp":"2026-09-17T10:07:00Z","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>a151b2d5ee7fd3108</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<status>completed</status>\n<summary>Agent \"Fix it\" is waiting on its own background work</summary>\n</task-notification>"}}""",
        }) s.Apply(chat.ProjectWithInputs(line, ++number, T0, context));
        await Assert.That(s.RunningCount).IsEqualTo(2);

        s.Apply(chat.ProjectWithInputs(restart, ++number, T0, context));

        await Assert.That(s.Rows.Select(r => r.State)).IsEquivalentTo(new[] { RunState.Stopped, RunState.Stopped });
        await Assert.That(s.Rows[0].StateText).IsEqualTo("stopped · 1h 39m");
    }

    /// A report the new process wrote can follow runs it started itself, so it stops only what was
    /// paused before it: those were the old process's, and no later process resumes them.
    [Test]
    public async Task A_reopen_stops_only_the_runs_paused_before_it() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1"), Waiting("c1", "a1", T0.AddMinutes(10))));
        s.Apply(Signals(Started("c2", T0.AddMinutes(35)), Detached("c2", "a2")));
        s.Apply(Signals(Shell("c3"), Detached("c3", "b3")));

        s.Apply(Signals(new RunSignal.Reopened(T0.AddMinutes(40))));

        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Stopped);
        await Assert.That(s.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(40));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Running);
        await Assert.That(s.Rows[2].State).IsEqualTo(RunState.Running);
    }

    /// A late notification can land on either side of the reopen, and the wait's own date decides.
    [Test]
    [Arguments(20, false)]
    [Arguments(60, true)]
    public async Task A_paused_run_reads_by_its_wait_against_the_reopen_in_any_order(int waitedAt, bool running) {
        RunSignal[] signals = [new RunSignal.Reopened(T0.AddMinutes(40)), Waiting(null, "a1", T0.AddMinutes(waitedAt))];
        foreach (var order in new[] { new[] { 0, 1 }, new[] { 1, 0 } }) {
            var s = new SessionRuns(Clock());
            s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
            foreach (var index in order) s.Apply(Signals(signals[index]));

            await Assert.That(Only(s).IsRunning).IsEqualTo(running).Because(string.Join(",", order));
        }
    }

    [Test]
    public async Task Clear_forgets_the_reopen_with_the_rows_it_read() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(new RunSignal.Reopened(T0.AddMinutes(30))));

        s.Clear();
        s.Apply(Signals(Started("c1"), Detached("c1", "a1"), Waiting(null, "a1", T0.AddMinutes(10))));

        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
    }

    /// The new process can start work before Claude records the old one's leftovers.
    [Test]
    public async Task A_transcript_with_a_new_run_ahead_of_the_report_keeps_it_running() {
        var chat = TranscriptChat.For("claude")!;
        var context = chat.CreateContext("a1", null);
        var s = new SessionRuns(Clock());
        var number = 0;
        foreach (var line in new[] {
            """{"type":"assistant","timestamp":"2026-09-17T10:00:00Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Fix it","subagent_type":"general-purpose"}}]}}""",
            """{"type":"user","timestamp":"2026-09-17T10:00:01Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":"launched"}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a151b2d5ee7fd3108"}}""",
            """{"type":"user","timestamp":"2026-09-17T10:07:00Z","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>a151b2d5ee7fd3108</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<status>completed</status>\n<summary>Agent \"Fix it\" is waiting on its own background work</summary>\n</task-notification>"}}""",
            """{"type":"assistant","timestamp":"2026-09-17T11:38:30Z","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_N","name":"Bash","input":{"command":"sleep 600","description":"Wait","run_in_background":true}}]}}""",
            """{"type":"user","timestamp":"2026-09-17T11:38:31Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_N","content":"Command running in background with ID: bnew."}]},"toolUseResult":{"backgroundTaskId":"bnew"}}""",
        }) s.Apply(chat.ProjectWithInputs(line, ++number, T0, context));

        s.Apply(chat.ProjectWithInputs("""{"type":"user","timestamp":"2026-09-17T11:39:00Z","origin":{"kind":"task-notification"},"promptSource":"system","message":{"role":"user","content":"<task-notification>\n<task-id>b7k2</task-id>\n<task-id>__orphan_summary__:shell</task-id>\n<status>stopped</status>\n<summary>1 background shell command tasks didn't finish before the previous session ended. First 20 task ids: b7k2.</summary>\n<note>No completion record was found for them in the previous session.</note>\n</task-notification>"}}""", ++number, T0, context));

        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Stopped);
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Running);
        await Assert.That(s.RunningCommandCount).IsEqualTo(1);
    }

    /// The local feed names a launch from its meta file after the lines are folded, so on a reopen it
    /// arrives once a resume owns the agent; it must leave the resumed run owning it.
    [Test]
    public async Task A_launchs_own_id_arriving_after_a_resume_leaves_the_resumed_run_owning_the_agent() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1"), Finished("c1", "a1", RunOutcome.Done, at: T0.AddMinutes(2))));
        s.Apply(Signals(Resumed("m1", "a1", T0.AddMinutes(5))));
        s.Identify("c1", "a1");

        s.Apply(Signals(Finished(null, "a1", RunOutcome.Done, at: T0.AddMinutes(20))));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Done);
        await Assert.That(s.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(2));
    }

    [Test]
    public async Task An_end_that_says_how_is_final_and_a_repeated_bare_stop_changes_nothing() {
        var said = new SessionRuns(Clock());
        said.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        said.Apply(Signals(Finished("c1", "a1", RunOutcome.Failed, at: T0.AddSeconds(10))));
        said.Apply(Signals(Stop("a1", T0.AddSeconds(20))));
        await Assert.That(Only(said).State).IsEqualTo(RunState.Failed);
        await Assert.That(Only(said).EndedAt).IsEqualTo(T0.AddSeconds(10));

        var bare = new SessionRuns(Clock());
        bare.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        bare.Apply(Signals(Stop("a1", T0.AddSeconds(10))));
        bare.Apply(Signals(Stop("a1", T0.AddSeconds(20))));
        await Assert.That(Only(bare).EndedAt).IsEqualTo(T0.AddSeconds(10));
        bare.Apply(Signals(Finished(null, "a1", RunOutcome.Stopped, at: T0.AddSeconds(30))));
        await Assert.That(Only(bare).State).IsEqualTo(RunState.Stopped);
        bare.Apply(Signals(Finished("c1", "a1", RunOutcome.Done, at: T0.AddSeconds(40))));
        await Assert.That(Only(bare).State).IsEqualTo(RunState.Stopped);
        await Assert.That(Only(bare).EndedAt).IsEqualTo(T0.AddSeconds(10));
    }

    [Test]
    public async Task Duplicate_started_detached_and_finished_change_nothing() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c1", name: "Other")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).Name).IsEqualTo("Explore");

        s.Apply(Signals(Detached("c1", "a1"), Detached("c1", "a1")));
        s.Apply(Signals(Finished("c1", "a1", at: T0.AddSeconds(10))));
        s.Apply(Signals(Finished("c1", "a1", RunOutcome.Failed, at: T0.AddSeconds(20))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddSeconds(10));

        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Detached("c1", "a1")));
        s.Apply(Result("c1", isError: true));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(changes).IsEqualTo(0);
    }

    [Test]
    public async Task A_signal_or_result_for_an_unknown_id_changes_nothing() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("zz", "a9"), Finished("zz", null), Finished(null, "a9"), Finished(null, null, RunOutcome.Stopped)));
        s.Apply(Result("zz"));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
        await Assert.That(Only(s).IsBackground).IsFalse();
    }

    [Test]
    public async Task A_finish_for_an_unseen_call_id_falls_back_to_the_bound_agent_id() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a")));
        await Assert.That(Only(s).IsBackground).IsTrue();
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished("c-unknown", "a", RunOutcome.Done, at: T0.AddMinutes(2))));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Done);
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task The_latest_detached_owns_the_agent_id_while_the_earlier_row_still_runs() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a")));
        s.Apply(Signals(Started("c2"), Detached("c2", "a")));
        await Assert.That(s.RunningCount).IsEqualTo(2);

        s.Apply(Signals(Finished(null, "a", RunOutcome.Done, at: T0.AddMinutes(2))));
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Done);
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(1);
    }

    static SessionRuns SecondLaunch() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1", T0)));
        s.Apply(Signals(Detached("c1", "a")));
        s.Apply(Signals(Finished("c1", "a", at: T0.AddMinutes(1))));
        s.Apply(Signals(Started("c2", T0.AddMinutes(2))));
        s.Apply(Signals(Detached("c2", "a")));
        return s;
    }

    [Test]
    public async Task A_second_launch_of_the_same_agent_is_a_second_row_and_the_id_follows_it() {
        var byCall = SecondLaunch();
        await Assert.That(byCall.Rows).Count().IsEqualTo(2);
        await Assert.That(byCall.Rows[0].State).IsEqualTo(RunState.Done);
        await Assert.That(byCall.Rows[1].State).IsEqualTo(RunState.Running);
        byCall.Apply(Signals(Finished("c2", "a", at: T0.AddMinutes(3))));
        await Assert.That(byCall.Rows[1].State).IsEqualTo(RunState.Done);

        var byAgent = SecondLaunch();
        byAgent.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(3))));
        await Assert.That(byAgent.Rows[0].State).IsEqualTo(RunState.Done);
        await Assert.That(byAgent.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(1));
        await Assert.That(byAgent.Rows[1].State).IsEqualTo(RunState.Done);
        await Assert.That(byAgent.Rows[1].EndedAt).IsEqualTo(T0.AddMinutes(3));
    }

    [Test]
    public async Task After_a_second_launch_stale_signals_for_the_first_leave_the_second_running() {
        var repeated = SecondLaunch();
        repeated.Apply(Signals(Finished("c1", "a", at: T0.AddMinutes(3))));
        await Assert.That(repeated.Rows[1].State).IsEqualTo(RunState.Running);

        var delayed = SecondLaunch();
        delayed.Apply(Signals(Detached("c1", "a")));
        delayed.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(3))));
        await Assert.That(delayed.Rows[1].State).IsEqualTo(RunState.Done);

        var older = SecondLaunch();
        older.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(1).AddSeconds(30))));
        await Assert.That(older.Rows[1].State).IsEqualTo(RunState.Running);
        await Assert.That(older.RunningCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_later_result_for_a_detached_call_finishes_or_fails_the_row() {
        var done = new SessionRuns(Clock());
        done.Apply(Signals(Started("c1")));
        done.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        await Assert.That(done.RunningCount).IsEqualTo(1);
        done.Apply(Result("c1", at: T0.AddSeconds(5)));
        await Assert.That(Only(done).State).IsEqualTo(RunState.Done);
        await Assert.That(Only(done).EndedAt).IsEqualTo(T0.AddSeconds(5));

        var failed = new SessionRuns(Clock());
        failed.Apply(Signals(Started("c1")));
        failed.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        failed.Apply(Result("c1", isError: true));
        await Assert.That(Only(failed).State).IsEqualTo(RunState.Failed);
    }

    [Test]
    public async Task Two_results_in_one_projection_result_one_detached_and_one_not() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        s.Apply(Mixed(
            [new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1"), new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c2")],
            Detached("c1", "a1")));
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Running);
        await Assert.That(s.Rows[0].IsBackground).IsTrue();
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Done);
        await Assert.That(s.RunningCount).IsEqualTo(1);
    }

    [Test]
    public async Task Clear_drops_the_rows_and_the_bindings() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("c1", "a1")));
        var changes = 0;
        s.Changed += () => changes++;
        s.Clear();
        await Assert.That(s.Rows).IsEmpty();
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Signals(Started("c2")));
        s.Apply(Signals(Finished(null, "a1")));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Running);
    }

    [Test]
    public async Task Session_over_presents_running_rows_as_stopped_without_ending_them() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        var changes = 0;
        s.Changed += () => changes++;

        s.SessionOver = true;
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Stopped);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("stopped");
        await Assert.That(s.Rows[0].Outcome).IsEqualTo(RunState.Running);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", null, at: T0.AddSeconds(30))));
        await Assert.That(s.Rows[0].State).IsEqualTo(RunState.Done);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("30s");

        s.Apply(Signals(Started("c3")));
        await Assert.That(s.Rows[2].State).IsEqualTo(RunState.Stopped);
        await Assert.That(s.RunningCount).IsEqualTo(0);

        s.SessionOver = false;
        await Assert.That(s.Rows[1].State).IsEqualTo(RunState.Running);
        await Assert.That(s.Rows[2].State).IsEqualTo(RunState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(2);
        s.SessionOver = false;
        await Assert.That(changes).IsEqualTo(4);
    }

    [Test]
    public async Task Running_count_follows_the_rows_and_rows_keep_arrival_order() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c2", name: "second"), Started("c1", name: "first"), Started("c3", name: "third")));
        await Assert.That(s.RunningCount).IsEqualTo(3);
        await Assert.That(s.Rows.Select(r => r.Name)).IsEquivalentTo(new[] { "second", "first", "third" }, CollectionOrdering.Matching);
        s.Apply(Result("c1"));
        s.Apply(Result("c3", isError: true));
        await Assert.That(s.RunningCount).IsEqualTo(1);
        await Assert.That(s.Rows.Select(r => r.Name)).IsEquivalentTo(new[] { "second", "first", "third" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_running_list_holds_the_rows_presenting_as_running_in_arrival_order() {
        var s = new SessionRuns(Clock());
        await Assert.That(s.Running).IsEmpty();

        s.Apply(Signals(Started("c1", name: "first"), Started("c2", name: "second"), Started("c3", name: "third")));
        await Assert.That(s.Running.Select(r => r.Name)).IsEquivalentTo(new[] { "first", "second", "third" }, CollectionOrdering.Matching);
        await Assert.That(ReferenceEquals(s.Running[1], s.Rows[1])).IsTrue();

        s.Apply(Result("c2"));
        await Assert.That(s.Running.Select(r => r.Name)).IsEquivalentTo(new[] { "first", "third" }, CollectionOrdering.Matching);

        s.SessionOver = true;
        await Assert.That(s.Running).IsEmpty();

        s.SessionOver = false;
        await Assert.That(s.Running.Select(r => r.Name)).IsEquivalentTo(new[] { "first", "third" }, CollectionOrdering.Matching);

        s.Clear();
        await Assert.That(s.Running).IsEmpty();
    }

    /// Taking a row out and putting it back would rebuild its container and restart its pulse,
    /// so a row that keeps running is never touched while others start and finish.
    [Test]
    public async Task A_row_that_keeps_running_keeps_its_place_while_others_start_and_finish() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2"), Started("c3")));
        var moved = new List<RunRow>();
        var resets = 0;
        s.Running.CollectionChanged += (_, e) => {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
            moved.AddRange((e.OldItems ?? Array.Empty<object>()).OfType<RunRow>());
            moved.AddRange((e.NewItems ?? Array.Empty<object>()).OfType<RunRow>());
        };

        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c2")], Started("c4")));
        s.Tick();

        await Assert.That(s.Running.Select(r => r.CallId)).IsEquivalentTo(new[] { "c1", "c3", "c4" }, CollectionOrdering.Matching);
        await Assert.That(resets).IsEqualTo(0);
        await Assert.That(moved.Select(r => r.CallId)).IsEquivalentTo(new[] { "c2", "c4" });
    }

    [Test]
    public async Task Every_presented_state_has_its_own_count_and_session_over_moves_running_to_stopped() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2"), Started("c3"), Started("c4"), Started("c5")));
        s.Apply(Result("c1"));
        s.Apply(Result("c2"));
        s.Apply(Result("c3", isError: true));
        s.Apply(Signals(Finished("c4", null, RunOutcome.Stopped)));
        await Assert.That(s.Count(RunState.Running)).IsEqualTo(1);
        await Assert.That(s.Count(RunState.Done)).IsEqualTo(2);
        await Assert.That(s.Count(RunState.Failed)).IsEqualTo(1);
        await Assert.That(s.Count(RunState.Stopped)).IsEqualTo(1);

        s.SessionOver = true;
        await Assert.That(s.Count(RunState.Running)).IsEqualTo(0);
        await Assert.That(s.Count(RunState.Stopped)).IsEqualTo(2);

        s.Clear();
        await Assert.That(s.Count(RunState.Done)).IsEqualTo(0);
        await Assert.That(s.Count(RunState.Stopped)).IsEqualTo(0);
    }

    /// The running count and the row count both stand still here, so only the per-state counts
    /// can tell a listener that the numbers it shows have moved.
    [Test]
    public async Task A_bare_stop_revised_to_failed_moves_the_counts_and_raises_changed() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        s.Apply(Signals(Stop("a1", T0.AddSeconds(62))));
        await Assert.That(s.Count(RunState.Done)).IsEqualTo(1);
        var changes = 0;
        s.Changed += () => changes++;

        s.Apply(Signals(Finished("c1", "a1", RunOutcome.Failed, at: T0.AddSeconds(90))));
        await Assert.That(s.Count(RunState.Done)).IsEqualTo(0);
        await Assert.That(s.Count(RunState.Failed)).IsEqualTo(1);
        await Assert.That(changes).IsEqualTo(1);
    }

    [Test]
    public async Task Elapsed_rolls_minutes_into_hours() {
        var clock = Clock();
        var s = new SessionRuns(clock);
        s.Apply(Signals(Started("c1", T0)));
        clock.Advance(TimeSpan.FromHours(25) + TimeSpan.FromMinutes(33) + TimeSpan.FromSeconds(30));
        s.Tick();
        await Assert.That(Only(s).StateText).IsEqualTo("running · 25h 33m");
    }

    [Test]
    public async Task Elapsed_runs_on_the_clock_for_a_running_row_and_freezes_at_its_end() {
        var clock = Clock();
        var s = new SessionRuns(clock);
        s.Apply(Signals(Started("c1", T0)));
        await Assert.That(Only(s).StateText).IsEqualTo("running · 0s");

        clock.Advance(TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(18));
        await Assert.That(Only(s).StateText).IsEqualTo("running · 0s");
        s.Tick();
        await Assert.That(Only(s).StateText).IsEqualTo("running · 6m 18s");

        var raised = new List<string?>();
        Only(s).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        s.Apply(Result("c1", at: T0.AddMinutes(7)));
        await Assert.That(Only(s).StateText).IsEqualTo("7m 00s");
        await Assert.That(raised).Contains(nameof(RunRow.State));
        await Assert.That(raised).Contains(nameof(RunRow.StateText));

        clock.Advance(TimeSpan.FromHours(1));
        s.Tick();
        await Assert.That(Only(s).StateText).IsEqualTo("7m 00s");

        var unstamped = new SessionRuns(clock);
        unstamped.Apply(Signals(Started("c9", clock.GetUtcNow())));
        clock.Advance(TimeSpan.FromSeconds(9));
        unstamped.Apply(Result("c9"));
        await Assert.That(Only(unstamped).StateText).IsEqualTo("9s");
    }

    /// One projection that ends a command and starts a subagent keeps the running total; the
    /// command count still drops, and its readers hear of it.
    [Test]
    public async Task A_command_ending_beside_a_new_subagent_still_reports_the_command_count() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1"), Detached("c1", "b1")));
        await Assert.That(s.RunningCommandCount).IsEqualTo(1);
        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Started("a1")));
        await Assert.That(s.RunningCount).IsEqualTo(1);
        await Assert.That(s.RunningCommandCount).IsEqualTo(0);
        await Assert.That(changes).IsEqualTo(1);
    }

    /// A start with no call names a subagent only when it is the one running subagent still unnamed: with
    /// two, the records' order says nothing about which is which. A command row is never named.
    [Test]
    public async Task A_start_with_no_call_names_only_a_lone_unnamed_subagent() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2", T0.AddSeconds(1)), Shell("c4")));
        s.Apply(Signals(Detached("c4", "b4"), Identified(null, "a1")));
        await Assert.That(s.Rows.All(row => !row.CanOpen)).IsTrue();

        s.Apply(Signals(Identified("c2", "a2"), Identified(null, "a1")));

        await Assert.That(string.Join(",", s.Rows.Select(row => row.AgentId ?? "-"))).IsEqualTo("a1,a2,-");
        await Assert.That(string.Join(",", s.Rows.Select(row => row.CanOpen))).IsEqualTo("True,True,False");
    }

    /// A detach names its own row by its own id, which leaves the held id's owner as the one candidate left.
    [Test]
    public async Task A_held_id_pairs_once_a_detach_names_the_other_candidate() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2", T0.AddSeconds(1)), Identified(null, "a1")));

        s.Apply(Signals(Detached("c2", "b2")));

        await Assert.That(string.Join(",", s.Rows.Select(row => row.AgentId ?? "-"))).IsEqualTo("a1,b2");
    }

    /// A candidate that ends could have owned a held id, so the id is dropped rather than placed on the one left;
    /// a fresh id then pairs with it.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_held_id_is_dropped_when_a_candidate_ends(bool byResult) {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2", T0.AddSeconds(1)), Identified(null, "a1")));

        s.Apply(byResult ? Result("c1", isError: true) : Signals(Finished("c1", null)));
        await Assert.That(s.Rows[1].AgentId).IsNull();

        s.Apply(Signals(Identified(null, "a2")));
        await Assert.That(s.Rows[1].AgentId).IsEqualTo("a2");
    }

    /// The drop happens at the end itself, so a later signal in the same projection cannot pair the held id first —
    /// whether the id was held before the projection or within it.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_held_id_is_dropped_before_a_later_signal_in_the_same_projection_pairs_it(bool heldWithin) {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2", T0.AddSeconds(1)), Started("c3", T0.AddSeconds(2))));
        if (!heldWithin) s.Apply(Signals(Identified(null, "a1")));

        s.Apply(heldWithin
            ? Signals(Identified(null, "a1"), Finished("c1", null), Identified("c3", "a3"))
            : Signals(Finished("c1", null), Identified("c3", "a3")));

        await Assert.That(s.Rows[1].AgentId).IsNull();
        await Assert.That(s.Rows[2].AgentId).IsEqualTo("a3");
    }

    /// The call's own id outranks a paired guess, and the agent's end then finds the right row.
    [Test]
    public async Task An_id_by_call_replaces_a_paired_guess_and_its_end_follows() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Identified(null, "guess"), Started("c2", T0.AddSeconds(1))));
        s.Apply(Signals(Identified("c1", "a1"), Identified("c2", "guess")));

        s.Apply(Signals(Finished(null, "guess")));

        await Assert.That(s.Rows[0].AgentId).IsEqualTo("a1");
        await Assert.That(s.Rows[0].IsEnded).IsFalse();
        await Assert.That(s.Rows[1].IsEnded).IsTrue();
    }

    [Test]
    public async Task A_launch_acknowledgement_names_a_background_subagent() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Detached("c1", "a1")));
        await Assert.That(Only(s).AgentId).IsEqualTo("a1");
        await Assert.That(Only(s).CanOpen).IsTrue();
    }

    [Test]
    public async Task An_id_read_by_call_names_that_row() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        s.Identify("c2", "a2");
        await Assert.That(s.Rows[1].AgentId).IsEqualTo("a2");
        await Assert.That(s.Rows[0].AgentId).IsNull();
    }

    /// A start record for an agent its own call already named moves nothing, so its end still finds that row.
    [Test]
    public async Task A_start_record_for_a_placed_agent_names_no_other_row() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("cB"), Detached("cB", "b")));
        s.Apply(Signals(Started("cF", T0.AddSeconds(1)), Identified(null, "b")));

        s.Apply(Signals(Finished(null, "b")));

        await Assert.That(s.Rows[1].AgentId).IsNull();
        await Assert.That(s.Rows[0].IsEnded).IsTrue();
        await Assert.That(s.Rows[1].IsEnded).IsFalse();
    }

    /// An id can beat its call's own line: by call it waits for that row, without one it waits until one row
    /// is the only candidate.
    [Test]
    public async Task An_id_that_arrives_before_its_row_names_it_once_it_starts() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Identified("c1", "a1"), Identified(null, "b")));

        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Started("c2", T0.AddSeconds(1))));

        await Assert.That(s.Rows[0].AgentId).IsEqualTo("a1");
        await Assert.That(s.Rows[1].AgentId).IsEqualTo("b");
    }
}
