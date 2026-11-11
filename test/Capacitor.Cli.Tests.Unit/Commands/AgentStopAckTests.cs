using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class AgentStopAckTests {
    const string Id = "0123456789abcdef0123456789abcdef";

    static string Outcomes(string text, bool missingIsStopped = false) =>
        string.Join(",", AgentCommand.ReadStopAck(text, Id, missingIsStopped).Select(o => $"{o.Id}:{o.Outcome}"));

    static bool Fails(string text, bool missingIsStopped = false) =>
        AgentCommand.ReadStopAck(text, Id, missingIsStopped).Any(o => o.Outcome.IsFailure());

    [Test]
    [Arguments("a\tstopped\nb\tstopped", "a:Stopped,b:Stopped", false)]
    [Arguments("a\tstopped\nb\tskipped", "a:Stopped,b:Skipped", false)]
    [Arguments("a\tstopped\nb\tfailed", "a:Stopped,b:Failed", true)]
    [Arguments("a\tskipped\nb\tbogus\nc", "a:Skipped,b:Failed,c:Failed", true)]
    [Arguments("", "", false)]
    public async Task Skipping_never_fails_and_an_unknown_or_missing_status_does(string text, string outcomes, bool fails) {
        await Assert.That(Outcomes(text)).IsEqualTo(outcomes);
        await Assert.That(Fails(text)).IsEqualTo(fails);
    }

    [Test]
    [Arguments(true, "AlreadyStopped", false)]
    [Arguments(false, "NoSuchAgent", true)]
    public async Task Missing_for_the_named_agent_depends_on_whether_everything_was_being_stopped(bool missingIsStopped, string outcome, bool fails) {
        await Assert.That(Outcomes($"{Id}\tmissing", missingIsStopped)).IsEqualTo($"{Id}:{outcome}");
        await Assert.That(Fails($"{Id}\tmissing", missingIsStopped)).IsEqualTo(fails);
    }

    [Test]
    [Arguments("other\tmissing")]
    [Arguments(Id + "\tmissing\textra")]
    public async Task Missing_that_names_another_agent_or_carries_extra_cells_is_a_failure(string text) =>
        await Assert.That(Fails(text, missingIsStopped: true)).IsTrue();

    [Test]
    [Arguments("a\trunning\t/repo", 3)]
    [Arguments("a\trunning\t/repo\treview\tflow-1\treviewer", 6)]
    [Arguments("a\trunning\t/repo\treview\tflow-1\treviewer\ttitle", 7)]
    [Arguments("a\trunning\t/repo\treview\tflow-1\treviewer\ttitle\tstate", 8)]
    public async Task Each_known_column_count_reads(string row, int columns) {
        var table = AgentCommand.ParseAgentTable(row + "\n\n");

        await Assert.That(table).IsNotNull();
        await Assert.That(table!.Single().Row.Id).IsEqualTo("a");
        await Assert.That(table!.Single().Columns).IsEqualTo(columns);
    }

    [Test]
    [Arguments("a\trunning")]
    [Arguments("a\trunning\t/repo\treview")]
    [Arguments("a\trunning\t/repo\treview\tflow-1")]
    [Arguments("a\trunning\t/repo\treview\tflow-1\treviewer\ttitle\tstate\textra")]
    [Arguments(" \trunning\t/repo")]
    public async Task One_unreadable_row_refuses_the_whole_table(string bad) =>
        await Assert.That(AgentCommand.ParseAgentTable($"a\trunning\t/repo\n{bad}")).IsNull();

    [Test]
    public async Task An_empty_table_reads_as_no_agents() =>
        await Assert.That(AgentCommand.ParseAgentTable("")!.Count).IsEqualTo(0);

    /// <summary>The daemon steers an attached session but doesn't host it, so no outcome may read as stopping one, and
    /// a failure goes to stderr whichever kind it was.</summary>
    [Test]
    [Arguments("Stopped", false)]
    [Arguments("Skipped", false)]
    [Arguments("AlreadyStopped", false)]
    [Arguments("NoSuchAgent", true)]
    [Arguments("Failed", true)]
    public async Task No_outcome_reads_as_stopping_a_steered_session(string name, bool error) {
        var outcome = Enum.Parse<StopAckOutcome>(name);
        var (steered, steeredError) = AgentCommand.StopLine(outcome, "mod-0123456789abcdef", steered: true);
        var (_, hostedError)        = AgentCommand.StopLine(outcome, Id, steered: false);

        await Assert.That(steered.Contains("stop", StringComparison.OrdinalIgnoreCase)).IsFalse().Because(steered);
        await Assert.That(steered).Contains("mod-0123456789abcdef");
        await Assert.That(steeredError).IsEqualTo(error);
        await Assert.That(hostedError).IsEqualTo(error);
    }
}
