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
        public List<string> SessionIds { get; init; } = [];

        public static WorkItemPartDetailDto Part(string id, string status, params string[] sessions) => new() {
            WorkItemId = id, Title = id == "p1" ? "Card form" : id, Status = status, SessionIds = [.. sessions],
            Sessions = [.. sessions.Select(s => new WorkItemPartSessionDto { SessionId = s, Title = "Spike the card form", Vendor = "codex" })],
        };

        public Task<WorkItemPartsDto?> ReadAsync(string workItemId, CancellationToken ct) => Task.FromResult<WorkItemPartsDto?>(
            new() { WorkItem = new() { WorkItemId = workItemId, Title = "Checkout" }, Goal = "Pay in two steps", Parts = Parts, SessionIds = SessionIds });
        public Task<bool> RenameAsync(string workItemId, string title, CancellationToken ct) => Record($"title {title}");
        public Task<bool> SetGoalAsync(string workItemId, string? goal, CancellationToken ct) => Record($"goal {goal ?? "<none>"}");
        public Task<bool> SetStatusAsync(string workItemId, string? status, CancellationToken ct) => Record($"status {workItemId} {status ?? "<links>"}");
        public Task<bool> OrderPartsAsync(string workItemId, IReadOnlyList<string> partIds, CancellationToken ct) => Record($"order {string.Join(",", partIds)}");
        public Task<bool> MoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) => Record($"move {sessionId} to {workItemId}");

        public async Task<string?> AddPartAsync(string workItemId, string title, CancellationToken ct) =>
            await Record($"add {title}") ? "p9" : null;

        Task<bool> Record(string call) {
            Calls.Add(call);
            return Task.FromResult(Accepts);
        }

        public Task<bool> MakePartAsync(string workItemId, string looseEndId, CancellationToken ct) => throw new NotSupportedException();
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

    /// The board leads with the sessions on no slice, then has a column for every status in workflow order, each there
    /// even when empty, and a page opened later starts on the view the viewer last chose.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_board_has_a_column_per_status_and_is_remembered() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi { Parts = [FakeApi.Part("p1", "done"), FakeApi.Part("p2", "open"), FakeApi.Part("p3", "done")] };
        using var directory = new FakeAgentDirectory();
        var flipped = new HashSet<string>();
        using var page = new WorkItemPageViewModel("w1", api, directory, TimeProvider.System, flipped);

        await Assert.That(page.Columns.Select(column => (column.Label, column.Count))).IsEquivalentTo([
            ("Other sessions", 0), ("Open", 1), ("In progress", 0), ("In review", 0), ("Blocked", 0), ("Done", 2),
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        await page.ShowBoardCommand.Execute();
        using var reopened = new WorkItemPageViewModel("w1", api, directory, TimeProvider.System, flipped);
        await Assert.That(reopened.IsBoard).IsTrue();

        await reopened.ShowListCommand.Execute();
        using var again = new WorkItemPageViewModel("w1", api, directory, TimeProvider.System, flipped);
        await Assert.That(again.IsBoard).IsFalse();
    });

    /// A session dropped on a status lane becomes a new slice named after it, holding it, in that status.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_session_dropped_on_a_status_becomes_a_slice_there() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi { SessionIds = ["s9"] };
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);
        var session = page.Columns.SelectMany(column => column.Sessions).Single();

        page.DropOn(session, page.Columns.Single(column => column.Status == WorkStatus.Blocked))!();
        await page.ConfirmDropCommand.Execute();

        await Assert.That(api.Calls).IsEquivalentTo(["add Session s9", "move s9 to p9", "status p9 blocked"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    });

    /// A slice is added in the status whose group the field was opened in, even one with no slices yet, and the field
    /// stays open for the next one. Open is never set, so links still decide a slice added there.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_slice_is_added_in_the_status_it_was_typed_into() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi();
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);

        await page.ComposeCommand.Execute(WorkStatus.InReview);
        await Assert.That(page.Groups.Single(group => group.Status == WorkStatus.InReview).IsComposing).IsTrue();
        page.NewPartTitle = " Review copy ";
        await page.AddPartCommand.Execute();
        await page.ComposeCommand.Execute(WorkStatus.Open);
        page.NewPartTitle = "Polish";
        await page.AddPartCommand.Execute();

        await Assert.That(api.Calls).IsEquivalentTo(["add Review copy", "status p9 in_review", "add Polish"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That((page.Composing, page.NewPartTitle)).IsEqualTo((WorkStatus.Open, ""));

        page.CloseComposer();
        await Assert.That(page.Groups.Any(group => group.IsComposing)).IsFalse();
    });

    /// A slice dropped on the other sessions moves each of its sessions there.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_slice_dropped_on_the_other_sessions_hands_them_its_sessions() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi { Parts = [FakeApi.Part("p1", "open", "s1", "s2")] };
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);

        page.DropOn(page.Columns[1].Parts.Single(), page.Columns[0])!();
        await page.ConfirmDropCommand.Execute();

        await Assert.That(api.Calls).IsEquivalentTo(["move s1 to w1", "move s2 to w1"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    });

    static string[] Ids(WorkItemPageGroupViewModel group) => [.. group.Parts.Select(part => part.Id)];

    static void Run(WorkItemPageViewModel page, WorkItemPagePart part, string label) => page.MenuFor(part).Single(item => item.Label == label).Run();

    /// A slice moved down takes its place at once and keeps it, though the read that follows still reports the old
    /// order (this fake never catches up).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_moved_slice_keeps_its_new_place_before_the_read_model_has_it() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi { Parts = [FakeApi.Part("p1", "open"), FakeApi.Part("p2", "open"), FakeApi.Part("p3", "open")] };
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);

        Run(page, page.Groups.Single().Parts[0], "Move down");

        await Assert.That(api.Calls).IsEquivalentTo(["order p2,p1,p3"]);
        await Assert.That(Ids(page.Groups.Single())).IsEquivalentTo(["p2", "p1", "p3"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    });

    /// A session dropped on another slice shows there at once, among its sessions newest first as the server lists
    /// them, and stays though the read that follows still reports it where it was (this fake never catches up); a
    /// refused move puts it back.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public Task A_dropped_session_shows_where_it_landed_until_refused(bool accepted) => AvaloniaSession.WithImmediateRxScheduler(async () => {
        static WorkItemPartDetailDto Active(WorkItemPartDetailDto part, int day) => part with {
            Sessions = [.. part.Sessions.Select(session => session with { LastActivityAt = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero) })],
        };
        var api = new FakeApi { Accepts = accepted, Parts = [Active(FakeApi.Part("p1", "open", "s1"), 2), Active(FakeApi.Part("p2", "open", "s2"), 1)] };
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);
        var parts = page.Groups.Single().Parts;

        page.DropOn(parts[0].Sessions.Single(), parts[1])!();

        await Assert.That(page.Groups.Single().Parts.Select(part => string.Join(",", part.Sessions.Select(session => session.SessionId))))
            .IsEquivalentTo(accepted ? new[] { "", "s1,s2" } : ["s1", "s2"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    });

    /// While a row is dragged the page holds its rebuilds, so a change made meanwhile shows only once the drag ends.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_change_during_a_drag_shows_once_the_drag_ends() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        using var directory = new FakeAgentDirectory();
        using var page = Page(new FakeApi(), directory);

        page.HoldRebuilds(true);
        await page.Groups.Single().Parts.Single().SetStatusCommand.Execute("in_review");

        await Assert.That(page.Groups.Single().Status).IsEqualTo(WorkStatus.InProgress);
        page.HoldRebuilds(false);
        await Assert.That(page.Groups.Single().Status).IsEqualTo(WorkStatus.InReview);
    });

    /// A refused order puts the slice back, with no message.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_refused_order_puts_the_slice_back() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi { Accepts = false, Parts = [FakeApi.Part("p1", "open"), FakeApi.Part("p2", "open")] };
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);

        Run(page, page.Groups.Single().Parts[1], "Move up");

        await Assert.That(Ids(page.Groups.Single())).IsEquivalentTo(["p1", "p2"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(page.Message).IsNull();
    });

    /// What each drop sends: a slice onto another status's group takes that status once confirmed, a session onto
    /// another slice moves there at once, a drop where the thing already is stays, and a slice can't go onto a slice.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("slice onto another slice", "p1", "p3", "<none>")]
    [Arguments("slice onto another status's group", "p1", "in_review", "asked, status p1 in_review")]
    [Arguments("slice onto its own status's group", "p1", "open", "<stay>")]
    [Arguments("session onto another slice", "s1", "p3", "move s1 to p3")]
    [Arguments("session onto its own slice", "s1", "p1", "<stay>")]
    public Task Each_drop_sends_what_it_changes(string _, string dragged, string target, string calls) =>
        AvaloniaSession.WithImmediateRxScheduler(async () => {
            var api = new FakeApi { Parts = [FakeApi.Part("p1", "open", "s1"), FakeApi.Part("p2", "open"), FakeApi.Part("p3", "in_review")] };
            using var directory = new FakeAgentDirectory();
            using var page = Page(api, directory);
            object Find(string key) =>
                page.Groups.FirstOrDefault(g => g.Key == key) as object
                ?? page.Groups.SelectMany(g => g.Parts).FirstOrDefault(p => p.Id == key) as object
                ?? page.Groups.SelectMany(g => g.Parts).SelectMany(p => p.Sessions).First(s => s.SessionId == key);

            var drop = page.DropOn(Find(dragged), Find(target));
            drop?.Invoke();
            var asked = page.DropPrompt is not null;
            if (asked) await page.ConfirmDropCommand.Execute();

            await Assert.That(drop is null ? "<none>" : drop == WorkItemPageViewModel.Stay ? "<stay>" : (asked ? "asked, " : "") + string.Join(";", api.Calls))
                .IsEqualTo(calls);
        });

    /// A drop onto a group sends nothing until confirmed, and nothing at all when cancelled.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_cancelled_drop_onto_a_group_sends_nothing() => AvaloniaSession.WithImmediateRxScheduler(async () => {
        var api = new FakeApi { Parts = [FakeApi.Part("p1", "open")] };
        using var directory = new FakeAgentDirectory();
        using var page = Page(api, directory);

        page.DropOn(page.Groups.Single().Parts.Single(), page.Columns.Single(column => column.Status == WorkStatus.Done))!();

        await Assert.That(page.DropPrompt).IsNotNull();
        await page.CancelDropCommand.Execute();
        await Assert.That(page.DropPrompt).IsNull();
        await Assert.That(api.Calls).IsEmpty();
    });
}
