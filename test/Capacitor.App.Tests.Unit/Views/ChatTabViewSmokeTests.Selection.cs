using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.Views;

public partial class ChatTabViewSmokeTests {
    const string SelectionFirst = """{"type":"assistant","message":{"content":[{"type":"text","text":"Alpha **café**."}]}}""";
    const string SelectionSecond = """{"type":"user","message":{"role":"user","content":"You 👩🏽‍💻 reviewed."}}""";

    static Point TranscriptTextPosition(Host host, ChatItemViewModel item, int offset) {
        var block = host.View.GetVisualDescendants().OfType<MarkdownView>()
            .Single(c => c.IsEffectivelyVisible && ReferenceEquals(c.DataContext, item)).SelectionBlocks().First().Block;
        PresentAndLocate(host, block);
        var rect = block.TextLayout.HitTestTextPosition(offset);
        return block.TranslatePoint(new Point(rect.X + block.Padding.Left + 0.1, rect.Center.Y + block.Padding.Top), host.Window)!.Value;
    }

    static void CopyShortcut(Window window) {
        var hotkey = Application.Current!.PlatformSettings!.HotkeyConfiguration.Copy[0];
        window.KeyPressQwerty(PhysicalKey.C, (RawInputModifiers)hotkey.KeyModifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Transcript_drag_copies_both_directions_and_survives_recycling() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var host = new Host(show: false);
        try {
            reading.Apply(new());
            var live = new TextBlock { IsHitTestVisible = false, Opacity = 0 };
            AutomationProperties.SetLiveSetting(live, AutomationLiveSetting.Polite);
            var names = new NameScope();
            names.Register(LiveAnnouncer.PoliteName, live);
            NameScope.SetNameScope(host.Window, names);
            host.Window.Content = null;
            host.Window.Content = new Panel { Children = { host.View, live } };
            host.Show();
            var path = Tmp.CreateFile("selection.jsonl", Enumerable.Repeat(UserLine, 180).Concat([
                SelectionFirst, CallLine(1), ResultLine(1), CallLine(2), ResultLine(2), SelectionSecond,
            ]).ToArray());
            await host.LoadAsync(path);
            var first = host.Chat.Items.OfType<AssistantTextItem>().Single();
            var last = host.Chat.Items.OfType<UserTurnItem>().Last();
            var summary = OnlyGroup(host).Summary;
            var jump = host.View.FindControl<Button>("JumpToLatest")!;
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            var clipboard = host.Window.Clipboard!;
            var expected = $"Alpha café.\n{summary}\nYou 👩🏽‍💻";
            foreach (var forward in new[] { true, false }) {
                var firstText = TranscriptTextPosition(host, first, 0);
                var lastText = TranscriptTextPosition(host, last, "You 👩🏽‍💻".Length);
                var (start, end) = forward ? (firstText, lastText) : (lastText, firstText);
                var viewportHeight = host.Scroll.Viewport.Height;
                await clipboard.SetTextAsync("before drag");
                host.Window.MouseMove(start);
                host.Window.MouseDown(start, MouseButton.Left);
                host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                if (forward) {
                    var during = host.Scroll.Offset.Y;
                    await host.AppendLinesAndTickAsync(path, UserLine, UserLine);
                    await Assert.That(host.Scroll.Offset.Y).IsEqualTo(during);
                    await Assert.That(host.Scroll.Viewport.Height).IsEqualTo(viewportHeight);
                    await Assert.That(jump.IsVisible).IsFalse();
                } else {
                    await Assert.That(jump.Focus()).IsTrue();
                }
                await Assert.That(host.Chat.Selection.CopyText()).IsEqualTo(expected);
                host.Window.MouseUp(end, MouseButton.Left);
                host.Settle();
                await Assert.That(jump.IsVisible).IsTrue();
                await Assert.That(host.View.TranscriptSelectionEnabled).IsTrue();
                await Assert.That(host.Chat.Selection.CopyText()).IsEqualTo(expected);
                await Assert.That(host.Window.FocusManager.GetFocusedElement()).IsSameReferenceAs(items);
                CopyShortcut(host.Window);
                await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo(expected);
                await Assert.That(live.Text).IsEqualTo("Copied");
                if (forward) {
                    host.Window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Shift);
                    CopyShortcut(host.Window);
                    await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo(expected[..^"👩🏽‍💻".Length]);
                    host.Scroll.Offset += new Vector(0, viewportHeight - host.Scroll.Viewport.Height);
                    host.Settle();
                }
            }

            var offset = host.Scroll.Offset.Y;
            await host.AppendLinesAndTickAsync(path, UserLine, UserLine, UserLine, UserLine);
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(offset);
            await Assert.That(jump.IsVisible).IsTrue();
            WheelUp(host);
            host.Settle();
            await Assert.That(items.GetRealizedContainers().Any(c => ReferenceEquals(c.DataContext, first))).IsFalse();
            CopyShortcut(host.Window);
            await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo(expected);

            Click(host, jump);
            await Assert.That(host.AtBottom()).IsTrue();
            await Assert.That(jump.IsVisible).IsFalse();
            var latestAction = (Control)host.Window.FocusManager.GetFocusedElement()!;
            await Assert.That(latestAction.GetSelfAndVisualAncestors().OfType<Control>()
                .Any(c => items.IndexFromContainer(c) == items.ItemCount - 1)).IsTrue();
            await Assert.That(items.Focus()).IsFalse();
            await clipboard.SetTextAsync("latest action sentinel");
            host.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            host.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            host.Settle();
            await Assert.That(await clipboard.TryGetTextAsync()).IsNotEqualTo("latest action sentinel");
        } finally {
            await CloseSelectionHostAsync(host, reading);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Transcript_shift_arrows_extend_over_rows_and_gate_rollback_clears_the_range() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var host = new Host();
        try {
            await host.LoadAsync(Tmp.CreateFile("keyboard-selection.jsonl", [SelectionFirst, SelectionSecond]));
            var list = host.View.FindControl<ItemsControl>("ChatItems")!;
            var jump = host.View.FindControl<Button>("JumpToLatest")!;
            var clipboard = host.Window.Clipboard!;

            void FocusFirst() {
                host.Composer.Focus();
                host.Window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.Shift);
                host.Settle();
            }

            PaneCycle.Attach(host.Window);
            await clipboard.SetTextAsync("sentinel");
            FocusFirst();
            host.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Shift);
            CopyShortcut(host.Window);
            await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("sentinel");
            await Assert.That(jump.IsVisible).IsFalse();

            reading.Apply(new(DesktopDensity.Compact));
            host.Settle();
            await Assert.That(host.View.TranscriptSelectionEnabled).IsTrue();
            FocusFirst();
            host.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Shift);
            await Assert.That(jump.Focus()).IsTrue();
            host.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            await Assert.That(host.Chat.Selection.CopyText()).IsEqualTo("Alpha café.\nY");
            await Assert.That(list.IsFocused).IsTrue();
            await Assert.That(jump.Focus()).IsTrue();
            CopyShortcut(host.Window);
            await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("Alpha café.\nY");

            host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await Assert.That(host.View.HasTranscriptSelection).IsFalse();
            await Assert.That(list.Focus()).IsFalse();
            host.Window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            host.Settle();
            await Assert.That(host.View.HasTranscriptSelection).IsFalse();
            await Assert.That(host.Window.FocusManager.GetFocusedElement()).IsTypeOf<Button>();
            host.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            host.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            host.Settle();
            await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("You 👩🏽‍💻 reviewed.");
            host.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            host.Settle();
            var firstAction = (Control)host.Window.FocusManager.GetFocusedElement()!;
            await Assert.That(firstAction.GetSelfAndVisualAncestors().OfType<Control>()
                .Any(c => list.IndexFromContainer(c) == 0)).IsTrue();
            host.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Shift);
            await Assert.That(host.View.HasTranscriptSelection).IsTrue();

            host.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            host.Settle();
            await Assert.That(host.View.HasTranscriptSelection).IsFalse();
            await Assert.That(host.Window.FocusManager.GetFocusedElement()).IsSameReferenceAs(firstAction);
            host.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            host.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            host.Settle();
            await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("Alpha **café**.");
            host.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Shift);
            await Assert.That(host.View.HasTranscriptSelection).IsTrue();

            reading.Apply(null);
            host.Settle();
            await clipboard.SetTextAsync("after rollback");
            CopyShortcut(host.Window);
            await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("after rollback");
            await Assert.That(jump.IsVisible).IsFalse();
        } finally {
            await CloseSelectionHostAsync(host, reading);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Select_all_from_plain_transcript_text_replaces_the_cross_row_range() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var host = new Host();
        try {
            reading.Apply(new());
            await host.LoadAsync(Tmp.CreateFile("selection-menu.jsonl", [
                SelectionFirst,
                """{"type":"user","message":{"content":"<bash-input>printf shown</bash-input>"}}""",
                SelectionSecond,
            ]));
            var first = host.Chat.Items.OfType<AssistantTextItem>().Single();
            var last = host.Chat.Items.OfType<UserTurnItem>().Single();
            host.Chat.Selection.Set(ChatSelection.Point.At(first, "Alpha café.", 0),
                ChatSelection.Point.At(last, last.Text, last.Text.Length));
            var plain = host.View.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Single(block => block.IsEffectivelyVisible && block.Text == "! printf shown");
            var flyout = (Flyout)plain.ContextFlyout!;
            void InvokeMenu(string label) {
                flyout.ShowAt(plain);
                host.Settle();
                var button = ((StackPanel)flyout.Content!).Children.OfType<Button>().Single(b => Equals(b.Content, label));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                host.Settle();
            }
            await Assert.That(host.View.HasTranscriptSelection).IsTrue();
            InvokeMenu("Select all");
            await Assert.That(host.View.HasTranscriptSelection).IsFalse();
            var clipboard = host.Window.Clipboard!;
            async Task VerifyCopyOrder(Action copy) {
                await clipboard.SetTextAsync("before");
                var release = new TaskCompletionSource();
                var earlier = MarkdownClipboard.InOrder(async () => {
                    await release.Task;
                    await clipboard.SetTextAsync("earlier");
                });
                try {
                    copy();
                    await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("before");
                } finally {
                    release.TrySetResult();
                    await earlier;
                    await MarkdownClipboard.InOrder(() => Task.CompletedTask);
                }
                await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("! printf shown");
            }
            await VerifyCopyOrder(() => InvokeMenu("Copy"));
            await Assert.That(plain.Focus()).IsTrue();
            await VerifyCopyOrder(() => CopyShortcut(host.Window));
        } finally {
            await CloseSelectionHostAsync(host, reading);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Dragging_onto_a_trailing_attachment_selects_through_the_last_visible_text() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var host = new Host(marks: false);
        try {
            reading.Apply(new());
            await host.LoadAsync(Tmp.CreateFile("attachment-selection.jsonl", [SelectionFirst, SelectionSecond]));
            var first = host.Chat.Items.OfType<AssistantTextItem>().Single();
            var last = host.Chat.Items.OfType<UserTurnItem>().Single();
            var rows = (Avalonia.Collections.AvaloniaList<ChatItemViewModel>)host.Chat.Items;
            rows.Insert(1, new UserTurnItem("", [new UserAttachment("middle.png", null)]) {
                Key = new(first.Key!.Value.Line, first.Key.Value.Index + 1),
            });
            rows.Add(new UserTurnItem("", [new UserAttachment("trailing.png", null)]) {
                Key = new(last.Key!.Value.Line, last.Key.Value.Index + 1),
            });
            host.Settle();
            var from = TranscriptTextPosition(host, first, 0);
            var to = PresentAndLocate(host, host.View.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Text == "trailing.png"));
            var clipboard = host.Window.Clipboard!;
            await clipboard.SetTextAsync("before drag");

            host.Window.MouseMove(from);
            host.Window.MouseDown(from, MouseButton.Left);
            host.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            host.Window.MouseUp(to, MouseButton.Left);
            CopyShortcut(host.Window);
            await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo($"Alpha café.\n{last.Text}");
        } finally {
            await CloseSelectionHostAsync(host, reading);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Folding_a_thinking_endpoint_clears_the_range_before_copy() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var host = new Host(marks: false);
        try {
            reading.Apply(new());
            await host.LoadAsync(Tmp.CreateFile("fold-selection.jsonl", [ThinkingLine, SelectionSecond]));
            var first = host.Chat.Items.OfType<AssistantThinkingItem>().Single();
            var last = host.Chat.Items.OfType<UserTurnItem>().Single();
            var summary = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("thinkingSummary"));
            var from = TranscriptTextPosition(host, first, 0);
            var to = TranscriptTextPosition(host, last, 3);
            host.Window.MouseMove(from);
            host.Window.MouseDown(from, MouseButton.Left);
            host.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            host.Window.MouseUp(to, MouseButton.Left);
            host.Settle();
            await Assert.That(host.View.HasTranscriptSelection).IsTrue();

            await Assert.That(summary.Focus(NavigationMethod.Tab)).IsTrue();
            host.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            host.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            host.Settle();
            using var after = host.Window.CaptureRenderedFrame();

            await Assert.That(after).IsNotNull();
            await Assert.That(first.IsExpanded).IsFalse();
            await Assert.That(host.View.HasTranscriptSelection).IsFalse();
        } finally {
            await CloseSelectionHostAsync(host, reading);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Jump_to_latest_in_an_ended_session_without_row_actions_keeps_transcript_focus() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var host = new Host(marks: false);
        try {
            reading.Apply(new());
            var path = Tmp.CreateFile("ended-selection.jsonl", [SelectionFirst]);
            await host.LoadAsync(path);
            host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path, Status = "Completed" });
            var first = host.Chat.Items.OfType<AssistantTextItem>().Single();
            var rows = (Avalonia.Collections.AvaloniaList<ChatItemViewModel>)host.Chat.Items;
            rows.AddRange(Enumerable.Range(0, 45).Select(_ => new SystemNoteItem("", "Session note")));
            host.Settle();
            host.Scroll.ScrollToEnd();
            host.Settle();
            var list = host.View.FindControl<ItemsControl>("ChatItems")!;
            var jump = host.View.FindControl<Button>("JumpToLatest")!;
            var text = host.Chat.Selection.Read(first);
            host.Chat.Selection.Set(ChatSelection.Point.At(first, text, 0), ChatSelection.Point.At(first, text, text.Length));
            host.Settle();
            await Assert.That(host.Composer.IsEffectivelyVisible).IsFalse();
            await Assert.That(list.ContainerFromIndex(0)).IsNull();
            await Assert.That(jump.Focus()).IsTrue();

            host.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            host.Window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            host.Settle();

            await Assert.That(jump.IsVisible).IsFalse();
            await Assert.That(host.Window.FocusManager.GetFocusedElement()).IsSameReferenceAs(list);
            await Assert.That(list.Focus()).IsTrue();
        } finally {
            await CloseSelectionHostAsync(host, reading);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Transcript_copy_rechecks_remote_access_when_a_clipboard_write_is_waiting() => RunOnUiAsync(async () => {
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var events = new FakeSessionEvents([
            RemoteFixtures.Event(0, CanonicalEventTypes.AssistantTextGenerated, """{"content":"First"}"""),
            RemoteFixtures.Event(1, CanonicalEventTypes.UserMessageReceived, """{"content":"Second"}"""),
        ]);
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote,
            Observable.Return(new ChatSessionInfo("Ended", "Ended", "gemini", null, null, null, true, "", "s1")), Observable.Return<string[]?>(null),
            new ViewModels.AcceptingChatPromptInput(), new NoAttachmentUploader(),
            key => new RemoteTranscriptFeed(key, "gemini", access, events.Reader, lane, time, _ => { }, prefetch: 0),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
        var reading = new DesktopReadingResources(Application.Current!);
        var view = new ChatTabView { DataContext = chat };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        var unblock = new TaskCompletionSource();
        try {
            reading.Apply(new());
            window.Show();
            await WaitUntilAsync(() => chat.Items.Count == 2, what: "the remote transcript");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var first = chat.Items.OfType<AssistantTextItem>().Single();
            var last = chat.Items.OfType<UserTurnItem>().Single();
            chat.Selection.Set(ChatSelection.Point.At(first, "First", 0), ChatSelection.Point.At(last, "Second", 6));
            await view.TranscriptSelectionCopy()!;
            var clipboard = window.Clipboard!;
            await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("First\nSecond");

            await clipboard.SetTextAsync("sentinel");
            _ = MarkdownClipboard.InOrder(() => unblock.Task);
            var pending = view.TranscriptSelectionCopy()!;
            access.OnNext(SessionAccessState.Denied);
            unblock.SetResult();
            await pending;
            await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("sentinel");
            await Assert.That(chat.Selection.CopyText()).IsNull();
        } finally {
            unblock.TrySetResult();
            try {
                window.Close();
                await chat.TeardownAsync();
            } finally {
                reading.Apply(null);
            }
        }
    });

    static async Task CloseSelectionHostAsync(Host host, DesktopReadingResources reading) {
        try {
            await host.CloseAsync();
        } finally {
            reading.Apply(null);
        }
    }
}
