using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Plans;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class WorkContextPlanProgressTests {
    sealed class Harness : IAsyncDisposable {
        readonly Subject<AgentStatusDto?> _presence = new();
        readonly SessionRuns _runs;
        public FakeTimeProvider Time { get; } = new();
        public FakePlanSource Plans { get; } = new();
        public FakePullRequestSource PrSource { get; }
        public PullRequestContextViewModel Prs { get; }
        public WorkContextViewModel Vm { get; }

        public Harness() {
            _runs = new(Time);
            Vm = new(_presence, new FakeWorkContextSource(), Time, new RecordingOpener(), _runs, plans: Plans);
            PrSource = new(Time);
            PrSource.Lifecycles[2] = "merged";
            Prs = new(_presence, PrSource, Time, new RecordingOpener(), () => { });
            Vm.PullRequests = Prs;
            Prs.SetForeground(true);
        }

        public async Task ShowAsync(string session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", int? select = 2) {
            _presence.OnNext(Agent("a1", "claude", false, sessionId: session));
            await (Vm.Plan.PendingReadForTesting ?? Task.CompletedTask);
            await SettlePrAsync();
            if (select is { } number) await SelectAsync(number);
        }

        public async Task SelectAsync(int number) {
            Prs.Selected = Prs.Choices.First(choice => choice.Subject.Number == number);
            await SettlePrAsync();
        }

        public async Task SettlePrAsync() {
            await WaitUntilAsync(() => Prs.CanReveal && !Prs.IsReading, what: "PR overview");
            Dispatcher.UIThread.RunJobs();
        }

        public async Task SetTasksAsync(params string[] statuses) {
            Plans.Enqueue(Read(statuses));
            Vm.Plan.Refresh();
            await (Vm.Plan.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async ValueTask DisposeAsync() {
            await Vm.TeardownAsync();
            await Prs.TeardownAsync();
            _presence.Dispose();
        }
    }

    static SessionPlansRead Read(params string[] statuses) => new(SessionPlansReadKind.Ready, [new() {
        PlanId = "plan", IsCurrent = true,
        Tasks = statuses.Select((status, i) => new PlanLedgerTaskDto {
            TaskId = $"t{i}", Ordinal = i, Title = $"Task {i}", Status = status,
        }).ToList(),
    }]);

    /// The default selection moves off a PR its overview reads as merged, so the merged PR here is
    /// never the selected one and the note must name it anyway.
    [Test]
    public Task A_merged_session_PR_is_named_while_another_is_selected_and_selection_does_not_move_the_note() => RunOnUiAsync(async () => {
        await using var h = new Harness();
        h.Plans.Enqueue(Read("pending", "in_progress", "completed", "skipped"));
        var changes = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        await h.ShowAsync(select: null);

        await Assert.That(h.Prs.Selected!.Subject.Number).IsEqualTo(1);
        await Assert.That(h.Vm.PlanMergeNote).IsEqualTo("PR #2 merged with 2 tasks still open. Merges don’t update the plan.");
        await Assert.That(changes).Contains(nameof(WorkContextViewModel.ShowsPlanMergeNote));
        await Assert.That(h.Vm.Plan.DoneCount).IsEqualTo(2);
        await Assert.That(h.Vm.Plan.Tasks[0].State).IsEqualTo(PlanTaskState.Pending);
        await h.Vm.Plan.ToggleCommand.Execute();
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsTrue();

        await h.SelectAsync(2);
        await Assert.That(h.Vm.PlanMergeNote).StartsWith("PR #2 merged");
        await h.SelectAsync(1);
        await Assert.That(h.Vm.PlanMergeNote).StartsWith("PR #2 merged");
    });

    /// The listing row's state ranks the merged PR out of the default, so its overview is never read
    /// and only the list can name it.
    [Test]
    public Task A_PR_the_list_marks_merged_is_named_without_reading_its_overview() => RunOnUiAsync(async () => {
        await using var h = new Harness();
        h.PrSource.Links = [h.PrSource.Links[0], h.PrSource.Links[1] with { Lifecycle = "merged" }];
        h.Plans.Enqueue(Read("pending"));
        await h.ShowAsync(select: null);
        await Assert.That(h.Prs.Selected!.Subject.Number).IsEqualTo(1);
        await Assert.That(h.Vm.PlanMergeNote).StartsWith("PR #2 merged with 1 task still open");
    });

    [Test]
    public Task Several_merged_PRs_are_all_named_newest_first() => RunOnUiAsync(async () => {
        await using var h = new Harness();
        h.PrSource.Lifecycles[1] = "merged";
        h.Plans.Enqueue(Read("pending"));
        await h.ShowAsync(select: 1);
        await h.SelectAsync(2);
        await Assert.That(h.Vm.PlanMergeNote).StartsWith("PRs #2 and #1 merged with 1 task still open");
    });

    [Test]
    public Task Plan_refresh_updates_the_note_and_settled_or_empty_plans_hide_it() => RunOnUiAsync(async () => {
        await using var h = new Harness();
        h.Plans.Enqueue(Read("pending", "in_progress"));
        await h.ShowAsync();
        await h.SetTasksAsync("completed", "pending");
        await Assert.That(h.Vm.PlanMergeNote).StartsWith("PR #2 merged with 1 task still open");
        await Assert.That(h.Vm.Plan.ShowsBody).IsFalse();
        await h.SetTasksAsync("completed", "skipped");
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsFalse();
        await h.SetTasksAsync();
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsFalse();
    });

    [Test]
    public Task Hidden_PR_content_and_a_new_session_do_not_leave_a_merge_note_behind() => RunOnUiAsync(async () => {
        await using var h = new Harness();
        h.Plans.Enqueue(Read("pending"));
        await h.ShowAsync();
        h.Prs.SetForeground(false);
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsFalse();
        await Assert.That(h.Vm.PlanMergeNote).IsEmpty();
        h.Prs.SetForeground(true);
        await h.SettlePrAsync();
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsTrue();
        h.PrSource.Lifecycles[2] = "open";
        h.Plans.Enqueue(Read("pending"));
        await h.ShowAsync("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        await Assert.That(h.Vm.ShowsPlanMergeNote).IsFalse();
    });
}
