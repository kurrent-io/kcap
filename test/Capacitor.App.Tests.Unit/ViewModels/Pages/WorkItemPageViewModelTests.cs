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
        public List<WorkItemPartDetailDto> Parts { get; init; } = [Part("p1", "in_progress", "s1")];

        public static WorkItemPartDetailDto Part(string id, string status, params string[] sessions) => new() {
            WorkItemId = id, Title = id == "p1" ? "Card form" : id, Status = status, SessionIds = [.. sessions],
            Sessions = [.. sessions.Select(s => new WorkItemPartSessionDto { SessionId = s, Title = "Spike the card form", Vendor = "codex" })],
        };

        public Task<WorkItemPartsDto?> ReadAsync(string workItemId, CancellationToken ct) => Task.FromResult<WorkItemPartsDto?>(
            new() { WorkItem = new() { WorkItemId = workItemId, Title = "Checkout" }, Goal = "Pay in two steps", Parts = Parts });
        public Task<bool> RenameAsync(string workItemId, string title, CancellationToken ct) => Record($"title {title}");
        public Task<bool> SetGoalAsync(string workItemId, string? goal, CancellationToken ct) => Record($"goal {goal ?? "<none>"}");
        public Task<bool> SetStatusAsync(string workItemId, string? status, CancellationToken ct) => Record($"status {workItemId} {status ?? "<links>"}");
        public Task<bool> OrderPartsAsync(string workItemId, IReadOnlyList<string> partIds, CancellationToken ct) => Record($"order {string.Join(",", partIds)}");
        public Task<bool> MoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) => Record($"move {sessionId} to {workItemId}");

        Task<bool> Record(string call) {
            Calls.Add(call);
            return Task.FromResult(Accepts);
        }

        public Task<bool> MakePartAsync(string workItemId, string looseEndId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> AddPartAsync(string workItemId, string title, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AddLinkAsync(string workItemId, string reference, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> CreateForSessionAsync(string sessionId, string title, CancellationToken ct) => throw new NotSupportedException();
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

    /// A session the app has not listed takes its title and agent from the work item's read.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_unlisted_session_is_named_by_the_work_items_read() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        using var directory = new FakeAgentDirectory();
        using var page = Page(new FakeApi(), directory);

        var session = page.Groups.Single().Parts.Single().Sessions.Single();

        await Assert.That((session.Title, session.Vendor)).IsEqualTo(("Spike the card form", "codex"));
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

    static string[] Ids(WorkItemPageGroupViewModel group) => [.. group.Parts.Select(part => part.Id)];

    /// A slice dropped below another takes its place at once, arriving there, and keeps it, though the read that follows
    /// still reports the old order (this fake never catches up).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_dropped_slice_keeps_its_new_place_before_the_read_model_has_it() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi { Parts = [FakeApi.Part("p1", "open"), FakeApi.Part("p2", "open"), FakeApi.Part("p3", "open")] };
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);
        var parts = page.Groups.Single().Parts;
        var arrived = new List<string>();
        page.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(page.Groups)) arrived.AddRange(page.Groups.SelectMany(g => g.Parts).Where(p => p.Arrives).Select(p => p.Id));
        };

        page.DropOn(parts[0], parts[2], after: true)!();

        await Assert.That(api.Calls).IsEquivalentTo(["order p2,p3,p1"]);
        await Assert.That(Ids(page.Groups.Single())).IsEquivalentTo(["p2", "p3", "p1"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(arrived).IsEquivalentTo(["p1"]);
    });

    /// A refused order puts the slice back, with no message.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_refused_order_puts_the_slice_back() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi { Accepts = false, Parts = [FakeApi.Part("p1", "open"), FakeApi.Part("p2", "open")] };
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);
        var parts = page.Groups.Single().Parts;

        page.DropOn(parts[1], parts[0], after: false)!();

        await Assert.That(Ids(page.Groups.Single())).IsEquivalentTo(["p1", "p2"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(page.Message).IsNull();
    });

    /// What each drop sends: a slice among another status's slices takes that status and its place, a session onto
    /// another slice moves there, and a drop where the thing already is sends nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("slice above the slice already below it", "p1", "p2", false, "")]
    [Arguments("slice above a slice of another status", "p1", "p3", false, "status p1 in_review;order p2,p1,p3")]
    [Arguments("slice onto another status's group", "p1", "in_review", false, "status p1 in_review")]
    [Arguments("slice onto its own status's group", "p1", "open", false, "")]
    [Arguments("session onto another slice", "s1", "p3", false, "move s1 to p3")]
    [Arguments("session onto its own slice", "s1", "p1", false, "")]
    public Task Each_drop_sends_what_it_changes(string _, string dragged, string target, bool after, string calls) =>
        AvaloniaSession.WithImmediateRxScheduler(async () => {
            var api = new FakeApi { Parts = [FakeApi.Part("p1", "open", "s1"), FakeApi.Part("p2", "open"), FakeApi.Part("p3", "in_review")] };
            using var directory = new FakeAgentDirectory();
            using var page = Page(api, directory);
            object Find(string key) =>
                page.Groups.FirstOrDefault(g => g.Key == key) as object
                ?? page.Groups.SelectMany(g => g.Parts).FirstOrDefault(p => p.Id == key) as object
                ?? page.Groups.SelectMany(g => g.Parts).SelectMany(p => p.Sessions).First(s => s.SessionId == key);

            var drop = page.DropOn(Find(dragged), Find(target), after);
            drop?.Invoke();

            await Assert.That(string.Join(";", api.Calls)).IsEqualTo(calls);
            await Assert.That(drop is null).IsEqualTo(calls.Length == 0);
        });
}
