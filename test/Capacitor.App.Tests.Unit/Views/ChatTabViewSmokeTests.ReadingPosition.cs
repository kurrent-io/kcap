using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.Views;

public partial class ChatTabViewSmokeTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Reading_position_reopens_from_disk_and_keeps_draft_focus_on_reflow() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var state = new AppStateStore(Tmp.PathTo("reading.json"));
        var path = Tmp.CreateFile("resume.jsonl", ReadingLines("Checkpoint"));
        var first = new Host(readingPositions: ReadingPositionsFor(state));
        ChatMessageKey key;
        double y;
        try {
            reading.Apply(new());
            await first.LoadAsync(path);
            await OpenReadingTarget(first, "Checkpoint 035");
            var list = first.View.FindControl<ItemsControl>("ChatItems")!;
            var panel = (ChatVirtualizingPanel)list.ItemsPanelRoot!;
            key = panel.GetReadingAnchor()!.Value.Key;
            first.Scroll.Offset += new Vector(0, 12);
            first.Settle();
            y = ReadingRowY(first, key)!.Value;
            await Assert.That(y).IsLessThan(-2);
        } finally {
            await first.CloseAsync();
        }

        var reopened = new Host(show: false, readingPositions: ReadingPositionsFor(state));
        reopened.Window.Content = null;
        var scale = new LayoutTransformControl { Child = reopened.View };
        reopened.Window.Content = scale;
        reopened.Show();
        try {
            await reopened.LoadAsync(path);
            await WaitUntilAsync(() => ReadingRowY(reopened, key) is { } at && Math.Abs(at - y) < 3, what: "saved intra-row offset");
            await Assert.That(reopened.AtBottom()).IsFalse();
            var jump = reopened.View.FindControl<Button>("JumpToLatest")!;
            await Assert.That(jump.TranslatePoint(default, reopened.Scroll)!.Value.Y + 0.5)
                .IsGreaterThanOrEqualTo(reopened.Scroll.Bounds.Height);
            reopened.Type("unsent draft");
            await reopened.AppendLinesAndTickAsync(path, Reply("A streamed reply"));
            await Assert.That(Math.Abs(ReadingRowY(reopened, key)!.Value - y)).IsLessThan(3);

            reading.Apply(new(DesktopDensity.Compact, DesktopReadingFont.Monospace));
            scale.LayoutTransform = new ScaleTransform(1.3, 1.3);
            reading.SetZoom(1.3);
            reopened.Settle();
            await Assert.That(ReadingRowY(reopened, key)).IsNotNull();
            await Assert.That(reopened.Composer.IsFocused).IsTrue();
            await Assert.That(reopened.Composer.Text).IsEqualTo("unsent draft");

            Click(reopened, jump);
            await Assert.That(reopened.AtBottom()).IsTrue();
        } finally {
            await CloseSelectionHostAsync(reopened, reading);
        }

        reading.Apply(new());
        var live = new Host(readingPositions: ReadingPositionsFor(state));
        try {
            await live.LoadAsync(path);
            await WaitUntilAsync(live.AtBottom, what: "explicit follow-live position");
            await live.AppendLinesAndTickAsync(path, Reply("Still following"));
            await Assert.That(live.AtBottom()).IsTrue();
        } finally {
            await CloseSelectionHostAsync(live, reading);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Reading_positions_isolate_switching_feeds_and_signed_in_users() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var state = new AppStateStore(Tmp.PathTo("switch.json"));
        using var subjects = new BehaviorSubject<string?>("reader-a");
        var positions = new ChatReadingPositions(state, "northwind", subjects);
        var a = Tmp.CreateFile("first.jsonl", ReadingLines("First"));
        var b = Tmp.CreateFile("second.jsonl", ReadingLines("Second"));
        var host = new Host(readingPositions: positions);
        try {
            reading.Apply(new());
            await host.LoadAsync(a);
            await OpenReadingTarget(host, "First 035");
            var saved = ((ChatVirtualizingPanel)host.View.FindControl<ItemsControl>("ChatItems")!.ItemsPanelRoot!).GetReadingAnchor()!.Value;
            await host.LoadAsync(b);
            await WaitUntilAsync(host.AtBottom, what: "another feed at the tail");
            await host.LoadAsync(a);
            await WaitUntilAsync(() => ReadingRowY(host, saved.Key) is { } y && Math.Abs(y + saved.Within) < 3, what: "return to first feed");

            subjects.OnNext("reader-b");
            await WaitUntilAsync(host.AtBottom, what: "another identity at the tail");
            subjects.OnNext("reader-a");
            await WaitUntilAsync(() => ReadingRowY(host, saved.Key) is { } y && Math.Abs(y + saved.Within) < 3, what: "return to first identity");
        } finally {
            await CloseSelectionHostAsync(host, reading);
        }

        var stockPositions = ReadingPositionsFor(state);
        stockPositions.PreviewEnabled = false;
        var stock = new Host(readingPositions: stockPositions);
        try {
            await stock.LoadAsync(a);
            await Assert.That(stock.AtBottom()).IsTrue();
            await Assert.That(stock.View.FindControl<Button>("JumpToLatest")!.IsVisible).IsFalse();
        } finally {
            await stock.CloseAsync();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Reading_position_reveals_the_saved_row_inside_a_folded_topic() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var path = Tmp.CreateFile("folded.jsonl", ReadingLines("Folded"));
        var positions = ReadingPositionsFor(new AppStateStore(Tmp.PathTo("folded-state.json")));
        var marks = new ChatMarks();
        var first = new Host(readingPositions: positions);
        ChatMessageKey key;
        string scope;
        ChatReadingPosition position;
        try {
            reading.Apply(new());
            await first.LoadAsync(path);
            var rows = first.Chat.Items.OfType<AssistantTextItem>().ToArray();
            key = rows[35].Key!.Value;
            marks.SetTopics(path, new Dictionary<ChatMessageKey, ChatTopicMark> {
                [rows[30].Key!.Value] = new(true, "Folded checkpoints"),
                [rows[55].Key!.Value] = new(false, null, End: true),
            });
            scope = first.Chat.ReadingScope("reader-a", path);
            position = new(key, 8, ChatReadingAnchor.Proof(rows[35]), first.Chat.ReadingSourceStamp);
        } finally {
            await first.CloseAsync();
        }

        positions.Remember(scope, position);
        await positions.FlushAsync();
        var reopened = new Host(show: false, readingPositions: positions, chatMarks: marks);
        reopened.Window.Content = null;
        var cover = new Border { Child = reopened.View, IsVisible = false };
        reopened.Window.Content = cover;
        reopened.Show();
        try {
            await reopened.LoadAsync(path);
            await WaitUntilAsync(() => reopened.Chat.ReadingFeedReady, what: "rows loaded behind the hidden host");
            reopened.Settle();
            await Assert.That(reopened.View.IsVisible).IsTrue();
            await Assert.That(reopened.View.IsEffectivelyVisible).IsFalse();
            cover.IsVisible = true;
            await WaitUntilAsync(() => ReadingRowY(reopened, key) is { } y && Math.Abs(y + 8) < 3, what: "revealed row in the folded topic");
            await Assert.That(reopened.Chat.Items.OfType<ChatTopicItem>().Single().IsExpanded).IsTrue();
        } finally {
            await CloseSelectionHostAsync(reopened, reading);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Reading_position_rejects_same_length_local_replacement() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var state = new AppStateStore(Tmp.PathTo("replacement.json"));
        var path = Tmp.CreateFile("replace.jsonl", ReadingLines("Original"));
        var host = new Host(readingPositions: ReadingPositionsFor(state));
        try {
            reading.Apply(new());
            await host.LoadAsync(path);
            await OpenReadingTarget(host, "Original 035");
            var length = new FileInfo(path).Length;
            File.WriteAllLines(path, ReadingLines("Replaced"));
            await Assert.That(new FileInfo(path).Length).IsEqualTo(length);
            await host.AppendLinesAndTickAsync(path);
            await WaitUntilAsync(() => host.Chat.Items[0] is AssistantTextItem { Text: "Replaced 000" }, what: "replacement rows");
            await WaitUntilAsync(host.AtBottom, what: "replacement at the tail");
        } finally {
            await CloseSelectionHostAsync(host, reading);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Reading_position_refreshes_the_proof_when_shell_output_arrives() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var positions = ReadingPositionsFor(new AppStateStore(Tmp.PathTo("shell.json")));
        var time = new FakeTimeProvider();
        var feed = new ViewModels.ChatPromptFeed();
        feed.Enqueue(new([new AcpEventEnvelope(Kind: AcpEventKind.SystemNote, Text: "first output", ToolKind: ChatDisplayKind.Shell)], [], []));
        feed.Enqueue(new([.. Enumerable.Range(0, 60).Select(i => new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: $"Reply {i}"))], [], []));
        var chat = new ChatTabViewModel("agent", AgentOrigin.Local,
            Observable.Return(new ChatSessionInfo("Shell", "Working", "claude", null, null, null, false, "", "shell-feed")),
            Observable.Return<string[]?>(null), new ViewModels.AcceptingChatPromptInput(), new NoAttachmentUploader(),
            _ => feed, new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), readingPositions: positions);
        var view = new ChatTabView { DataContext = chat };
        var window = new Window { Content = view, Width = 900, Height = 600 };
        var scope = chat.ReadingScope("reader-a", "shell-feed");
        try {
            reading.Apply(new());
            window.Show();
            await WaitUntilAsync(() => {
                time.Advance(ChatTabViewModel.PollInterval);
                return chat.ReadingFeedReady;
            }, what: "shell transcript");
            var list = view.FindControl<ItemsControl>("ChatItems")!;
            window.UpdateLayout();
            var center = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(center, new Vector(0, 1000));
            var shell = chat.Items.OfType<ShellCommandItem>().Single();
            await WaitUntilAsync(async () => (await positions.ReadAsync(scope))?.Key == shell.SelectionKey, what: "captured shell anchor");

            // A commandless row derives its proof from the current output.
            shell.Output = "updated output";
            window.Content = null;
            await Assert.That((await positions.ReadAsync(scope))?.Proof).IsEqualTo(ChatReadingAnchor.Proof(shell));
        } finally {
            window.Close();
            await chat.TeardownAsync();
            reading.Apply(null);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Remote_reading_position_pages_from_the_composer_and_withdraws_after_the_view_detaches() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var state = new AppStateStore(Tmp.PathTo("remote.json"));
        using var subjects = new BehaviorSubject<string?>("reader-a");
        var positions = new ChatReadingPositions(state, "northwind", subjects);
        var scope = positions.Key("reader-a", AgentOrigin.Remote, "session", "session");
        var saved = new ChatReadingPosition(new ChatMessageKey(12, 0), 11, ChatReadingAnchor.Proof(new AssistantTextItem("Event 012")));
        await positions.ReadAsync(scope);
        positions.Remember(scope, saved);
        await positions.FlushAsync();

        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 170).Select(i =>
            RemoteFixtures.Event(i, CanonicalEventTypes.AssistantTextGenerated, $$"""{"content":"Event {{i:D3}}"}"""))]);
        var lane = new FakeServerLane();
        var chat = new ChatTabViewModel("agent", AgentOrigin.Remote,
            Observable.Return(new ChatSessionInfo("Ended", "Ended", "gemini", null, null, null, true, "", "session")), Observable.Return<string[]?>(null),
            new ViewModels.AcceptingChatPromptInput(), new NoAttachmentUploader(),
            key => new RemoteTranscriptFeed(key, "gemini", access, server.Reader, lane, time, _ => { }, window: 100, prefetch: 0),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), readingPositions: positions);
        var view = new ChatTabView { DataContext = chat };
        var window = new Window { Content = view, Width = 900, Height = 600 };
        try {
            reading.Apply(new());
            window.Show();
            var list = view.FindControl<ItemsControl>("ChatItems")!;
            await WaitUntilAsync(() => {
                window.UpdateLayout();
                return list.ItemsPanelRoot is ChatVirtualizingPanel panel && panel.GetReadingAnchor()?.Key.Line == 70;
            }, what: "nearest loaded event");
            await Assert.That(server.RequestCount).IsEqualTo(1);
            await Assert.That(await positions.ReadAsync(scope)).IsEqualTo(saved);

            // An ended session keeps paging on demand. Invoke the composer route without enabling input.
            var page = new KeyEventArgs { Key = Key.PageUp, RoutedEvent = InputElement.KeyDownEvent };
            view.FindControl<TextBox>("ComposerInput")!.RaiseEvent(page);
            await Assert.That(page.Handled).IsTrue();
            await WaitUntilAsync(() => chat.Items.Count == 170, what: "earlier rows after Page Up at the top");
            await Assert.That(server.RequestCount).IsEqualTo(2);

            window.Content = null;
            access.OnNext(SessionAccessState.Denied);
            await WaitUntilAsync(async () => await positions.ReadAsync(scope) is null, what: "withdrawn remote position");
            await WaitUntilAsync(async () => (await state.LoadAsync()).ChatReadingPositions?.ContainsKey(scope) == false,
                what: "withdrawn position on disk");

            var other = positions.Key("reader-b", AgentOrigin.Remote, "session", "session");
            var unopened = positions.Key("reader-b", AgentOrigin.Remote, "unopened", "unopened");
            positions.Remember(scope, saved);
            positions.Remember(other, saved);
            positions.Remember(unopened, saved);
            await positions.FlushAsync();
            subjects.OnNext("reader-b");
            chat.InvalidateReadingPosition("session");
            chat.InvalidateReadingPosition("unopened");
            await WaitUntilAsync(async () => (await state.LoadAsync()).ChatReadingPositions is { } records
                    && !records.ContainsKey(other) && !records.ContainsKey(unopened),
                what: "withdrawn positions for the current account of both established and unopened feeds");
            await Assert.That(await positions.ReadAsync(scope)).IsEqualTo(saved);
        } finally {
            window.Close();
            await chat.TeardownAsync();
            reading.Apply(null);
        }
    });

    static ChatReadingPositions ReadingPositionsFor(IAppStateStore state) =>
        new(state, "northwind", Observable.Return<string?>("reader-a"));

    static string[] ReadingLines(string prefix) => [.. Enumerable.Range(0, 100).Select(i => Reply($"{prefix} {i:D3}"))];

    static double? ReadingRowY(Host host, ChatMessageKey key) {
        host.Settle();
        return host.View.FindControl<ItemsControl>("ChatItems")!.GetRealizedContainers()
            .FirstOrDefault(c => c.DataContext is AssistantTextItem item && item.Key == key)?.TranslatePoint(default, host.Scroll)?.Y;
    }

    static async Task OpenReadingTarget(Host host, string query) {
        await WaitUntilAsync(host.AtBottom, what: "initial transcript");
        host.View.OpenFind();
        host.Window.KeyTextInput(query);
        host.Settle();
        await WaitUntilAsync(() => host.Chat.Find.Current is not null, what: "find target");
        host.Chat.Find.Close();
        host.Settle();
    }
}
