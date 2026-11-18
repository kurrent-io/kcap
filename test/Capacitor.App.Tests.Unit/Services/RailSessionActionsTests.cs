using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit.Services;

public class RailSessionActionsTests {
    const string SessionId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    sealed class Harness {
        public readonly RecordingNotifier Notifier = new();
        public readonly List<(string Id, AgentOrigin Origin)> Renamed = [];
        public readonly List<string> Hidden = [];
        public readonly List<string> Disabled = [];
        public readonly List<string> Asked = [];
        public readonly List<(string Id, string Label, string Kind, AgentOrigin Origin)> Stopped = [];
        public int Refreshes;
        public bool Confirm = true;
        public ProcessResult Result = new(0, "Session x disabled. Recording stopped and server data deleted.\n", "", false);

        public RailSessionActions Build() => new(
            rename: (id, origin) => Renamed.Add((id, origin)),
            hide: (id, _) => { Hidden.Add(id); return Task.FromResult(Result); },
            disable: (id, _) => { Disabled.Add(id); return Task.FromResult(Result); },
            confirmDelete: label => { Asked.Add(label); return Task.FromResult(Confirm); },
            stop: (id, label, kind, origin) => Stopped.Add((id, label, kind, origin)),
            notifier: Notifier,
            refresh: () => { Refreshes++; return Task.CompletedTask; },
            shutdown: CancellationToken.None);
    }

    static AgentRow Row(AgentOrigin origin, string? sessionId = SessionId, bool? ownerIsViewer = null, string status = "Running") =>
        new($"{origin}:a1", origin, "a1", sessionId, "agent", "claude", status, DateTime.UtcNow, "/repo", "Fix it", null, null,
            null, null, null, null, "repo", "repo", "repo", "repo", OwnerIsViewer: ownerIsViewer);

    public static IEnumerable<Func<(AgentRow Row, bool Owns)>> Ownership() {
        yield return () => (Row(AgentOrigin.Local), true);
        yield return () => (Row(AgentOrigin.Local, sessionId: null), false);
        yield return () => (Row(AgentOrigin.Pending), false);
        yield return () => (Row(AgentOrigin.Remote), false);
        yield return () => (Row(AgentOrigin.Remote, ownerIsViewer: true), true);
        yield return () => (Row(AgentOrigin.Server, ownerIsViewer: false), false);
        yield return () => (Row(AgentOrigin.Server, ownerIsViewer: true, status: "Completed"), true);
    }

    [Test]
    [MethodDataSource(nameof(Ownership))]
    public async Task Session_items_need_a_recorded_session_the_viewer_owns(AgentRow row, bool owns) {
        await Assert.That(RailSessionActions.OwnsSession(row)).IsEqualTo(owns);
    }

    public static IEnumerable<Func<(AgentRow Row, bool Can)>> Terminable() {
        yield return () => (Row(AgentOrigin.Local), true);
        yield return () => (Row(AgentOrigin.Pending, sessionId: null), true);
        yield return () => (Row(AgentOrigin.Local, status: "Completed"), false);
        yield return () => (Row(AgentOrigin.Local, status: AgentRow.DaemonDownStatus), false);
        yield return () => (Row(AgentOrigin.Remote), false);
        yield return () => (Row(AgentOrigin.Remote, ownerIsViewer: true), true);
        yield return () => (Row(AgentOrigin.Server, ownerIsViewer: true), false);
    }

    /// A server row is a session no daemon hosts here or remotely, so there is no agent to stop.
    [Test]
    [MethodDataSource(nameof(Terminable))]
    public async Task Terminate_needs_a_running_agent_a_daemon_hosts(AgentRow row, bool can) {
        await Assert.That(RailSessionActions.CanTerminate(row)).IsEqualTo(can);
    }

    public static IEnumerable<Func<(AgentRow Row, bool Can)>> Deletable() {
        yield return () => (Row(AgentOrigin.Local), true);
        yield return () => (Row(AgentOrigin.Server, ownerIsViewer: true, status: "Completed"), true);
        yield return () => (Row(AgentOrigin.Server, ownerIsViewer: true), false);
        yield return () => (Row(AgentOrigin.Remote, ownerIsViewer: true), false);
    }

    /// `kcap disable` stops only this machine's recorders; another machine's live recorder would send rows back.
    [Test]
    [MethodDataSource(nameof(Deletable))]
    public async Task Delete_waits_for_a_session_live_elsewhere_to_end(AgentRow row, bool can) {
        await Assert.That(RailSessionActions.CanDelete(row)).IsEqualTo(can);
    }

    [Test]
    public async Task Rename_opens_the_row_on_its_own_lane() {
        var h = new Harness();
        h.Build().Rename(Row(AgentOrigin.Remote, ownerIsViewer: true));
        await Assert.That(h.Renamed).IsEquivalentTo(new[] { ("a1", AgentOrigin.Remote) });
    }

    [Test]
    public async Task Make_private_hides_the_session_and_says_so() {
        var h = new Harness();
        await h.Build().MakePrivateAsync(Row(AgentOrigin.Local), "Fix it");
        await Assert.That(h.Hidden).IsEquivalentTo(new[] { SessionId });
        await Assert.That(h.Notifier.Notified).IsEquivalentTo(new[] { "Fix it is now private" });
    }

    [Test]
    public async Task A_failed_hide_names_the_cli_reason() {
        var h = new Harness { Result = new ProcessResult(1, "", "Server returned 403\nmore", false) };
        await h.Build().MakePrivateAsync(Row(AgentOrigin.Local), "Fix it");
        await Assert.That(h.Notifier.Notified).IsEquivalentTo(new[] { "Couldn't make Fix it private: Server returned 403" });
    }

    [Test]
    public async Task A_declined_delete_runs_nothing() {
        var h = new Harness { Confirm = false };
        await h.Build().DeleteAsync(Row(AgentOrigin.Local), "Fix it");
        await Assert.That(h.Asked).IsEquivalentTo(new[] { "Fix it" });
        await Assert.That(h.Disabled).IsEmpty();
        await Assert.That(h.Refreshes).IsEqualTo(0);
        await Assert.That(h.Notifier.Notified).IsEmpty();
    }

    [Test]
    public async Task A_confirmed_delete_disables_the_session_and_rereads_the_listing() {
        var h = new Harness();
        await h.Build().DeleteAsync(Row(AgentOrigin.Server, ownerIsViewer: true, status: "Completed"), "Fix it");
        await Assert.That(h.Disabled).IsEquivalentTo(new[] { SessionId });
        await Assert.That(h.Refreshes).IsEqualTo(1);
        await Assert.That(h.Notifier.Notified).IsEquivalentTo(new[] { "Deleted Fix it from kcap" });
    }

    public static IEnumerable<Func<(ProcessResult Result, string Told)>> DeleteOutcomes() {
        yield return () => (new ProcessResult(0, "Session x disabled. Server data deleted.\n", "", false), "Deleted Fix it from kcap");
        yield return () => (new ProcessResult(0, "Session x disabled. Server data deleted.\nThis agent is still running.\n", "", false),
            "Deleted Fix it from kcap. This agent is still running");
        yield return () => (new ProcessResult(0, "Session disabled locally (hooks silenced). Server data not deleted.\n", "connection refused\n", false),
            "Fix it: Session disabled locally (hooks silenced). Server data not deleted. connection refused");
        yield return () => (new ProcessResult(1, "Session x disabled. Server data deleted.\nA send may still land.\n", "", false),
            "Fix it: Session x disabled. Server data deleted. A send may still land");
        yield return () => (new ProcessResult(1, "", "Server returned 500\n", false), "Couldn't delete Fix it: Server returned 500");
        yield return () => (new ProcessResult(-1, "", "", true), "Couldn't delete Fix it: kcap did not answer in time");
    }

    /// A zero exit can still leave the server's data in place, so only the CLI's own summary earns "Deleted".
    [Test]
    [MethodDataSource(nameof(DeleteOutcomes))]
    public async Task Delete_reports_only_what_kcap_disable_confirmed(ProcessResult result, string told) {
        var h = new Harness { Result = result };
        await h.Build().DeleteAsync(Row(AgentOrigin.Local), "Fix it");
        await Assert.That(h.Notifier.Notified).IsEquivalentTo(new[] { told });
    }

    [Test]
    public async Task Another_users_session_is_never_touched() {
        var h = new Harness();
        var actions = h.Build();
        var theirs = Row(AgentOrigin.Server, ownerIsViewer: false);
        actions.Rename(theirs);
        await actions.MakePrivateAsync(theirs, "x");
        await actions.DeleteAsync(theirs, "x");
        await Assert.That(h.Renamed).IsEmpty();
        await Assert.That(h.Hidden).IsEmpty();
        await Assert.That(h.Asked).IsEmpty();
        await Assert.That(h.Disabled).IsEmpty();
    }

    [Test]
    public async Task Terminate_stops_a_pending_launch_on_the_local_daemon() {
        var h = new Harness();
        h.Build().Terminate(Row(AgentOrigin.Pending, sessionId: null), "Fix it");
        await Assert.That(h.Stopped).IsEquivalentTo(new[] { ("a1", "Fix it", "agent", AgentOrigin.Local) });
    }
}
