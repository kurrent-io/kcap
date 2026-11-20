using System.Collections.Frozen;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Pages;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.ViewModels.Pages;

public class WorkItemPageViewModelTests {
    sealed class FakeApi : IWorkItemPageApi {
        public List<string> Calls { get; } = [];
        public bool Accepts { get; set; } = true;
        public IObservable<System.Reactive.Unit> Changed => Observable.Never<System.Reactive.Unit>();
        public IObservable<IReadOnlySet<string>> SuggestedSessions => Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty);

        public Task<WorkItemPartsDto?> ReadAsync(string workItemId, CancellationToken ct) => Task.FromResult<WorkItemPartsDto?>(
            new() {
                WorkItem = new() { WorkItemId = workItemId, Title = "Checkout" }, Goal = "Pay in two steps",
                Parts = [new() { WorkItemId = "p1", Title = "Card form", Status = "in_progress" }],
            });
        public Task<bool> RenameAsync(string workItemId, string title, CancellationToken ct) => Record($"title {title}");
        public Task<bool> SetGoalAsync(string workItemId, string? goal, CancellationToken ct) => Record($"goal {goal ?? "<none>"}");
        public Task<bool> SetStatusAsync(string workItemId, string? status, CancellationToken ct) => Record($"status {workItemId} {status ?? "<links>"}");

        Task<bool> Record(string call) {
            Calls.Add(call);
            return Task.FromResult(Accepts);
        }

        public Task<bool> MakePartAsync(string workItemId, string looseEndId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> AddPartAsync(string workItemId, string title, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AddLinkAsync(string workItemId, string reference, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> CreateForSessionAsync(string sessionId, string title, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> MoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RemoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkItemSuggestionDto?> ReadSuggestionAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AcceptSuggestionAsync(string workItemId, string sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DismissSuggestionAsync(string workItemId, string sessionId, CancellationToken ct) => throw new NotSupportedException();
    }

    static WorkItemPageViewModel Page(FakeApi api, FakeAgentDirectory directory) =>
        new("w1", api, directory, TimeProvider.System, new HashSet<string>());

    /// The title and goal edit in place: an unchanged one sends nothing, a blank goal clears it, and the header shows
    /// the saved text at once.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_title_and_goal_save_in_place_and_a_blank_goal_clears_it() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi();
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);

        await page.TitleEditor.BeginEditCommand.Execute();
        await Assert.That(page.TitleEditor.Draft).IsEqualTo("Checkout");
        page.TitleEditor.Draft = " Checkout ";
        await page.TitleEditor.SaveCommand.Execute();
        await page.GoalEditor.BeginEditCommand.Execute();
        await Assert.That(page.GoalEditor.Draft).IsEqualTo("Pay in two steps");
        page.GoalEditor.Draft = " ";
        await page.GoalEditor.SaveCommand.Execute();

        await Assert.That(api.Calls).IsEquivalentTo(["goal <none>"]);
        await Assert.That(page.Goal).IsNull();
        await Assert.That(page.GoalEditor.IsEditing).IsFalse();

        await page.TitleEditor.BeginEditCommand.Execute();
        page.TitleEditor.Draft = "One-step checkout";
        await page.TitleEditor.SaveCommand.Execute();

        await Assert.That(api.Calls).IsEquivalentTo(["goal <none>", "title One-step checkout"]);
        await Assert.That(page.Title).IsEqualTo("One-step checkout");
    });

    /// A refused save keeps the field open for another try and the page says what failed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_refused_save_keeps_the_field_open() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi { Accepts = false };
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);

        await page.GoalEditor.BeginEditCommand.Execute();
        page.GoalEditor.Draft = "Pay in one step";
        await page.GoalEditor.SaveCommand.Execute();

        await Assert.That(page.GoalEditor.IsEditing).IsTrue();
        await Assert.That(page.Goal).IsEqualTo("Pay in two steps");
        await Assert.That(page.Message).IsEqualTo("Couldn't save the goal");
    });

    /// A slice moves to its new status group as soon as the change is recorded, though the read that follows still
    /// reports the old status (this fake never catches up), and goes back to the read's status when the viewer hands
    /// it back to its links.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_set_status_moves_the_slice_before_the_read_model_has_it() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi();
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);

        await page.Groups.Single().Parts.Single().SetStatusCommand.Execute("in_review");

        await Assert.That(api.Calls).IsEquivalentTo(["status p1 in_review"]);
        await Assert.That(page.Groups.Single().Status).IsEqualTo(WorkStatus.InReview);

        await page.Groups.Single().Parts.Single().SetStatusCommand.Execute("");

        await Assert.That(page.Groups.Single().Status).IsEqualTo(WorkStatus.InProgress);
    });
}
