using Capacitor.Cli.Core.Plans;
using TUnit.Assertions.Enums;

namespace Capacitor.Client.Tests.Unit;

public class PlanBoardTests {
    static PlanLedgerTaskDto Task(int ordinal, string status = "pending", string? id = null, string title = "t", string? note = null) =>
        new() { TaskId = id, Ordinal = ordinal, Title = title, Status = status, Note = note };

    static SessionPlansRead Ready(params SessionPlanDto[] plans) => new(SessionPlansReadKind.Ready, plans);

    [Test]
    public async Task The_current_plan_wins_over_an_earlier_one() {
        var board = PlanBoard.From(Ready(
            new SessionPlanDto { PlanId = "a", Tasks = [Task(1, id: "from-a")] },
            new SessionPlanDto { PlanId = "b", IsCurrent = true, Tasks = [Task(1, id: "from-b")] }));

        await Assert.That(board!.Tasks.Select(t => t.TaskId)).IsEquivalentTo(new[] { "from-b" });
    }

    [Test]
    public async Task Without_a_current_plan_the_first_is_shown() {
        var board = PlanBoard.From(Ready(
            new SessionPlanDto { PlanId = "a", Tasks = [Task(1, id: "from-a")] },
            new SessionPlanDto { PlanId = "b", Tasks = [Task(1, id: "from-b")] }));

        await Assert.That(board!.Tasks.Select(t => t.TaskId)).IsEquivalentTo(new[] { "from-a" });
    }

    [Test]
    public async Task Tasks_come_in_ordinal_order() {
        var board = PlanBoard.From(Ready(new SessionPlanDto { Tasks = [Task(3, id: "c"), Task(1, id: "a"), Task(2, id: "b")] }));

        await Assert.That(board!.Tasks.Select(t => t.TaskId).ToArray()).IsEquivalentTo(new[] { "a", "b", "c" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_task_with_no_id_is_keyed_by_its_ordinal() {
        var board = PlanBoard.From(Ready(new SessionPlanDto { Tasks = [Task(3, id: null), Task(4, id: "")] }));

        await Assert.That(board!.Tasks.Select(t => t.TaskId).ToArray()).IsEquivalentTo(new[] { "#3", "#4" });
    }

    [Test]
    public async Task Skipped_counts_as_done() {
        var board = PlanBoard.From(Ready(new SessionPlanDto {
            Tasks = [Task(1, "completed"), Task(2, "skipped"), Task(3, "in_progress"), Task(4, "pending")],
        }));

        await Assert.That(board!.DoneCount).IsEqualTo(2);
        await Assert.That(board.Tasks.Select(t => t.State).ToArray()).IsEquivalentTo(new[] {
            PlanTaskState.Completed, PlanTaskState.Skipped, PlanTaskState.InProgress, PlanTaskState.Pending,
        });
    }

    [Test]
    public async Task An_unknown_status_reads_as_pending() {
        var board = PlanBoard.From(Ready(new SessionPlanDto { Tasks = [Task(1, "blocked")] }));

        await Assert.That(board!.Tasks[0].State).IsEqualTo(PlanTaskState.Pending);
    }

    [Test]
    public async Task A_blank_note_is_absent() {
        var board = PlanBoard.From(Ready(new SessionPlanDto { Tasks = [Task(1, note: "  "), Task(2, note: "why")] }));

        await Assert.That(board!.Tasks[0].Note).IsNull();
        await Assert.That(board.Tasks[1].Note).IsEqualTo("why");
    }

    [Test]
    public async Task Ready_with_no_plans_is_null() => await Assert.That(PlanBoard.From(Ready())).IsNull();

    [Test]
    public async Task A_read_that_is_not_ready_is_null() {
        await Assert.That(PlanBoard.From(SessionPlansRead.Of(SessionPlansReadKind.Unreachable))).IsNull();
        await Assert.That(PlanBoard.From(SessionPlansRead.Of(SessionPlansReadKind.SignedOut))).IsNull();
    }
}
