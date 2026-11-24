using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.Views;

public partial class ChatTabViewSmokeTests {
    static ChatTurnNavigator Navigator(ChatTabView view) => view.GetVisualDescendants().OfType<ChatTurnNavigator>().Single();
    static Button MapButton(ChatTurnNavigator navigator) => navigator.FindControl<Button>("TurnMapButton")!;
    static ListBox TurnOutline(ChatTurnNavigator navigator) => navigator.FindControl<ListBox>("TurnList")!;

    static void TurnPreview(bool enabled) {
        var resources = Application.Current!.Resources;
        if (enabled) resources[ChatTabView.TurnNavigatorResource] = true;
        else resources.Remove(ChatTabView.TurnNavigatorResource);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Navigator_keeps_the_thumb_and_inspector_divider_operable_in_the_workspace() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        reading.Apply(new DesktopAppearancePreferences());
        try {
            await using var scene = await DocsShotScenes.MainWindowAsync(arrange: vm => {
                var chat = ((WorkspaceViewModel)vm.CurrentWorkspace!).Chat!;
                var rows = (Avalonia.Collections.AvaloniaList<ChatItemViewModel>)chat.Items;
                rows.AddRange(Enumerable.Range(1, 160).Select(i => new UserTurnItem($"Compare retry policy {i}")));
                return Task.CompletedTask;
            });
            var window = scene.Window;
            window.Width = 1400;
            window.Height = 760;
            window.Show();
            void Present() {
                for (var frame = 0; frame < 3; frame++) {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
            }
            Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            void Drag(Point origin, Vector delta) {
                window.MouseMove(origin);
                window.MouseDown(origin, MouseButton.Left);
                window.MouseMove(origin + delta);
                window.MouseUp(origin + delta, MouseButton.Left);
                Present();
            }

            Present();
            var workspace = window.GetVisualDescendants().OfType<WorkspaceView>().Single();
            var splitter = workspace.FindControl<GridSplitter>("ContextSplitter")!;
            var chatView = workspace.GetVisualDescendants().OfType<ChatTabView>().Single();
            var scroll = chatView.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "ChatScroll");
            var bar = scroll.GetTemplateDescendants().OfType<ScrollBar>().Single(x => x.Orientation == Orientation.Vertical);
            var thumb = bar.GetTemplateDescendants().OfType<Thumb>().Single();
            await Assert.That(splitter.IsEffectivelyVisible).IsTrue();
            var oldOffset = scroll.Offset.Y;
            Drag(Center(thumb), new Vector(0, -75));
            await Assert.That(scroll.Offset.Y).IsLessThan(oldOffset);

            TurnPreview(true);
            Present();
            var pane = ((MainWindow)window).Panes;
            var width = pane.ContextWidth;
            var trackCenter = Center(bar).X;
            oldOffset = scroll.Offset.Y;
            Drag(Center(thumb).WithX(trackCenter), new Vector(0, -55));
            await Assert.That(scroll.Offset.Y).IsLessThan(oldOffset);
            await Assert.That(pane.ContextWidth).IsEqualTo(width);

            var page = bar.GetTemplateDescendants().OfType<RepeatButton>().Single(x => x.Name == "PART_PageDownButton");
            var pagePoint = Center(page).WithX(trackCenter);
            oldOffset = scroll.Offset.Y;
            window.MouseDown(pagePoint, MouseButton.Left);
            window.MouseUp(pagePoint, MouseButton.Left);
            Present();
            await Assert.That(scroll.Offset.Y).IsGreaterThan(oldOffset);
            oldOffset = scroll.Offset.Y;
            window.MouseWheel(pagePoint, new Vector(0, 1));
            Present();
            await Assert.That(scroll.Offset.Y).IsLessThan(oldOffset);

            var navigator = Navigator(chatView);
            MapButton(navigator).Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Present();
            await Assert.That(TurnOutline(navigator).IsKeyboardFocusWithin).IsTrue();
            window.KeyPressQwerty(PhysicalKey.F6, RawInputModifiers.None);
            Present();
            var composer = chatView.FindControl<Grid>("ComposerColumn")!;
            await Assert.That(composer.IsKeyboardFocusWithin).IsTrue();
            await Assert.That(TurnOutline(navigator).IsEffectivelyVisible).IsFalse();

            Drag(Center(splitter), new Vector(-35, 0));
            await Assert.That(pane.ContextWidth).IsGreaterThan(width);
            await Assert.That(chatView.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(x => x.Name == "ChatScroll")).IsSameReferenceAs(scroll);
        } finally {
            TurnPreview(false);
            reading.Apply(null);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Navigator_keeps_native_page_thumb_and_wheel_and_can_be_disabled_while_open() => RunOnUiAsync(async () => {
        var host = new Host(marks: false);
        var app = Application.Current!;
        var reading = new DesktopReadingResources(app);
        var palette = new ClassicPreviewPalette(app);
        var materials = new DesktopModernMaterials(app);
        try {
            await host.AttachAsync(Tmp.CreateFile("navigator.jsonl", string.Join('\n', UnevenHistory()) + "\n"));
            host.Chat.ComposerText = "Synthetic draft";
            await Assert.That(host.View.GetVisualDescendants().OfType<ChatTurnNavigator>().Any()).IsFalse();
            var original = host.Scroll;
            await Assert.That(original.AllowAutoHide).IsTrue();

            palette.TryApply(ClassicAppearance.Light);
            reading.Apply(new DesktopAppearancePreferences(DesktopDensity.Compact));
            app.Resources[DesktopScrollbarPreference.ResourceKey] = true;
            ReducedMotion.OverrideForTesting(true);
            TurnPreview(true);
            host.Settle();
            var navigator = Navigator(host.View);
            var button = MapButton(navigator);
            var disclosure = ControlAutomationPeer.CreatePeerForElement(button).GetProvider<IExpandCollapseProvider>()!;
            await Assert.That(disclosure.ExpandCollapseState).IsEqualTo(ExpandCollapseState.Collapsed);
            disclosure.Expand();
            host.Settle();
            await Assert.That(TurnOutline(navigator).IsEffectivelyVisible).IsTrue();
            await Assert.That(disclosure.ExpandCollapseState).IsEqualTo(ExpandCollapseState.Expanded);
            foreach (var (mode, highContrast, supportsShadow) in new[] {
                (DesktopVisualMode.Modern, false, true),
                (DesktopVisualMode.Modern, false, false),
                (DesktopVisualMode.Modern, true, true),
                (DesktopVisualMode.Classic, false, true),
            }) {
                materials.Apply(mode, highContrast, supportsShadow);
                host.Settle();
                await Assert.That(TurnOutline(navigator).IsKeyboardFocusWithin).IsTrue();
                await Assert.That(disclosure.ExpandCollapseState).IsEqualTo(ExpandCollapseState.Expanded);
            }
            disclosure.Collapse();
            host.Settle();
            await Assert.That(button.IsFocused).IsTrue();
            await Assert.That(disclosure.ExpandCollapseState).IsEqualTo(ExpandCollapseState.Collapsed);
            var map = navigator.FindControl<ChatTurnMap>("TurnMap")!;
            var bar = original.GetTemplateDescendants().OfType<ScrollBar>().Single(x => x.Orientation == Orientation.Vertical);
            await Assert.That(bar.AllowAutoHide).IsFalse();
            var pageAbove = bar.GetTemplateDescendants().OfType<RepeatButton>().Single(x => x.Name == "PART_PageUpButton");
            var panel = host.View.NavigatorPanel!;
            int VisibleRow() => panel.ProjectedRowAt(original.Offset.Y);
            var initialRow = VisibleRow();
            Click(host, pageAbove);
            var pageRow = VisibleRow();
            await Assert.That(pageRow).IsLessThan(initialRow);

            var thumb = bar.GetTemplateDescendants().OfType<Thumb>().Single();
            var thumbOrigin = PresentAndLocate(host, thumb);
            host.Window.MouseDown(thumbOrigin, MouseButton.Left);
            host.Window.MouseMove(thumbOrigin.WithY(thumbOrigin.Y - 65));
            host.Window.MouseUp(thumbOrigin.WithY(thumbOrigin.Y - 65), MouseButton.Left);
            host.Settle();
            await Assert.That(VisibleRow()).IsLessThan(pageRow);

            var gutter = PresentAndLocate(host, button);
            host.Window.MouseMove(gutter);
            host.Settle();
            await Assert.That(map.IsWide).IsTrue();
            await Assert.That(TurnOutline(navigator).IsEffectivelyVisible).IsFalse();
            var before = original.Offset.Y;
            var animationFrames = 0;
            host.View.RequestSmoothFrame = (_, _) => animationFrames++;
            host.Window.MouseWheel(gutter, new Vector(0, 1));
            host.Settle();
            await Assert.That(original.Offset.Y).IsLessThan(before - 40);
            await Assert.That(animationFrames).IsEqualTo(0);

            Click(host, button);
            var list = TurnOutline(navigator);
            await Assert.That(list.IsEffectivelyVisible).IsTrue();
            var selected = list.SelectedIndex;
            await Assert.That(selected).IsGreaterThan(1);
            host.Press(PhysicalKey.ArrowDown);
            await Assert.That(list.SelectedIndex).IsEqualTo(selected + 1);
            host.Press(PhysicalKey.ArrowUp);
            await Assert.That(list.SelectedIndex).IsEqualTo(selected);
            var target = (ChatTurnOutlineEntry)list.SelectedItem!;
            Click(host, list.ContainerFromIndex(list.SelectedIndex)!);
            await Assert.That(list.IsEffectivelyVisible).IsFalse();
            await Assert.That(host.View.NavigatorTranscript.ContainerFromItem(target.Destination!.Row)).IsNotNull();
            button.Focus(NavigationMethod.Tab);
            host.Press(PhysicalKey.Enter);
            // Activation can leave focus on the map button, so it must route the next key too.
            await Assert.That(button.Focus(NavigationMethod.Pointer)).IsTrue();
            host.Press(PhysicalKey.ArrowDown);
            await Assert.That(list.SelectedIndex).IsEqualTo(1);
            await Assert.That(button.Focus(NavigationMethod.Pointer)).IsTrue();
            host.Press(PhysicalKey.Enter);
            await Assert.That(list.IsEffectivelyVisible).IsFalse();
            button.Focus(NavigationMethod.Tab);
            host.Press(PhysicalKey.Enter);
            await Assert.That(button.Focus(NavigationMethod.Pointer)).IsTrue();
            host.Press(PhysicalKey.Escape);
            await Assert.That(button.IsFocused).IsTrue();
            await Assert.That(list.IsEffectivelyVisible).IsFalse();
            await Assert.That(disclosure.ExpandCollapseState).IsEqualTo(ExpandCollapseState.Collapsed);
            host.Press(PhysicalKey.Enter);
            host.Press(PhysicalKey.Tab);
            await Assert.That(navigator.HasFocus).IsFalse();

            button.Focus(NavigationMethod.Tab);
            host.Press(PhysicalKey.Enter);
            var readingRow = host.View.NavigatorPanel!.GetReadingAnchor()?.Row;
            await Assert.That(readingRow).IsNotNull();
            TurnPreview(false);
            host.Settle();
            await Assert.That(host.View.GetVisualDescendants().OfType<ChatTurnNavigator>().Any()).IsFalse();
            await Assert.That(host.Scroll).IsSameReferenceAs(original);
            await Assert.That(original.AllowAutoHide).IsTrue();
            await Assert.That(disclosure.ExpandCollapseState).IsEqualTo(ExpandCollapseState.Collapsed);
            app.Resources[DesktopScrollbarPreference.ResourceKey] = false;
            host.Settle();
            await Assert.That(bar.AllowAutoHide).IsFalse();
            await Assert.That(host.Window.FocusManager.GetFocusedElement()).IsNotNull();
            await Assert.That(host.View.NavigatorPanel.GetReadingAnchor()?.Row).IsSameReferenceAs(readingRow);
            await Assert.That(host.Chat.ComposerText).IsEqualTo("Synthetic draft");
        } finally {
            TurnPreview(false);
            materials.Apply(DesktopVisualMode.Classic, highContrast: false);
            ReducedMotion.OverrideForTesting(null);
            reading.Apply(null);
            palette.TryApply(ClassicAppearance.Original);
            await host.CloseAsync();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Navigator_bounds_10000_streaming_rows_and_keeps_a_keyboard_jump_aligned() => RunOnUiAsync(async () => {
        var host = new Host(marks: false);
        var app = Application.Current!;
        var reading = new DesktopReadingResources(app);
        var palette = new ClassicPreviewPalette(app);
        try {
            palette.TryApply(ClassicAppearance.Dark);
            reading.Apply(new DesktopAppearancePreferences());
            TurnPreview(true);
            var lines = Enumerable.Range(1, 5000).SelectMany(i => new[] {
                UserLine.Replace("hello", $"Synthetic prompt {i}"), AssistantLinkLine,
            });
            var path = Tmp.CreateFile("stream.jsonl", string.Join('\n', lines) + "\n");
            await host.LoadAsync(path);
            await Assert.That(host.Chat.Items.Count).IsEqualTo(10000);
            var navigator = Navigator(host.View);
            var map = navigator.FindControl<ChatTurnMap>("TurnMap")!;
            var button = MapButton(navigator);
            button.Focus(NavigationMethod.Tab);
            host.Press(PhysicalKey.Enter);
            var list = TurnOutline(navigator);
            host.Press(PhysicalKey.End);
            await Assert.That(list.ItemCount).IsEqualTo(5002);
            await Assert.That(list.GetRealizedContainers().Count()).IsLessThan(40);
            var last = (ChatTurnOutlineEntry)list.SelectedItem!;
            await Assert.That(last.Detail).IsEqualTo("Synthetic prompt 5000");
            host.Press(PhysicalKey.Home);
            host.Press(PhysicalKey.ArrowDown);
            var second = (ChatTurnOutlineEntry)list.SelectedItem!;
            await Assert.That(second.Detail).IsEqualTo("Synthetic prompt 2");
            var peer = ControlAutomationPeer.CreatePeerForElement(list.ContainerFromIndex(list.SelectedIndex)!);
            await Assert.That(peer.GetName()).Contains("Synthetic prompt 2");
            await Assert.That(peer.GetProvider<IInvokeProvider>()).IsNotNull();
            host.Press(PhysicalKey.Enter);
            host.Settle();

            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            double? TargetY() => items.ContainerFromItem(second.Destination!.Row)?.TranslatePoint(default, host.Scroll)?.Y;
            var y = TargetY();
            await Assert.That(y).IsNotNull();
            await Assert.That(y!.Value).IsBetween(0, 16);
            for (var batch = 0; batch < 3; batch++)
                await host.AppendAndTickAsync(path, 16);
            host.Settle();
            await Assert.That(host.Chat.Items.Count).IsEqualTo(10048);
            await Assert.That(Math.Abs(TargetY()!.Value - y.Value)).IsLessThanOrEqualTo(8);
            using var draw = new DrawingGroup().Open();
            map.Render(draw);
            await Assert.That(map.PaintedBuckets).IsBetween(1, ChatTurnMap.MaxBuckets);

            button.Focus(NavigationMethod.Tab);
            host.Press(PhysicalKey.Enter);
            host.Press(PhysicalKey.End);
            await Assert.That(list.ItemCount).IsEqualTo(5050);
            host.Press(PhysicalKey.Enter);
            host.Settle();
            await Assert.That(host.Scroll.Offset.Y).IsGreaterThan(host.Scroll.Extent.Height / 2);
            await Assert.That(items.GetRealizedContainers().Count()).IsLessThan(60);
        } finally {
            TurnPreview(false);
            reading.Apply(null);
            palette.TryApply(ClassicAppearance.Original);
            await host.CloseAsync();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Navigator_reveals_a_folded_prompt_and_keeps_the_bookmark_and_find_query() => RunOnUiAsync(async () => {
        var host = new Host();
        try {
            TurnPreview(true);
            var lines = Enumerable.Range(1, 20).SelectMany(i => new[] {
                UserLine.Replace("hello", $"Review patch {i}"), AssistantLinkLine,
            });
            await host.LoadAsync(Tmp.CreateFile("fold.jsonl", string.Join('\n', lines) + "\n"));
            var first = host.Chat.Items.OfType<UserTurnItem>().First();
            var second = host.Chat.Items.OfType<UserTurnItem>().Skip(1).First();
            host.Chat.Topics.ToggleChange(first);
            host.Chat.Topics.ToggleChange(host.Chat.Items.OfType<UserTurnItem>().Skip(4).First());
            var fold = host.Chat.Topics.All[0];
            await host.Chat.Bookmarks.ToggleMessageCommand.Execute(second);
            var bookmark = second.Bookmark;
            host.Chat.Find.Query = "Review patch";
            await fold.ToggleCommand.Execute();
            host.Settle();
            await Assert.That(fold.IsExpanded).IsFalse();

            var navigator = Navigator(host.View);
            MapButton(navigator).Focus(NavigationMethod.Tab);
            host.Press(PhysicalKey.Enter);
            host.Press(PhysicalKey.Home);
            host.Press(PhysicalKey.ArrowDown);
            var list = TurnOutline(navigator);
            await Assert.That(((ChatTurnOutlineEntry)list.SelectedItem!).Detail).Contains("Folded topic");
            var peer = ControlAutomationPeer.CreatePeerForElement(list.ContainerFromIndex(list.SelectedIndex)!);
            peer.GetProvider<IInvokeProvider>()!.Invoke();
            host.Settle();
            await Assert.That(fold.IsExpanded).IsTrue();
            await Assert.That(second.Bookmark).IsSameReferenceAs(bookmark);
            await Assert.That(host.Chat.Find.Query).IsEqualTo("Review patch");
            await Assert.That(host.View.FindControl<ItemsControl>("ChatItems")!.ContainerFromItem(second)).IsNotNull();
        } finally {
            TurnPreview(false);
            await host.CloseAsync();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Navigator_remote_partial_turn_loads_history_and_tracks_access_without_new_rows() => RunOnUiAsync(async () => {
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var server = new FakeSessionEvents([.. Enumerable.Range(0, 350).Select(i => i % 30 == 0
            ? RemoteFixtures.Event(i, CanonicalEventTypes.UserMessageReceived, $$"""{"content":"Prompt {{i}}"}""")
            : RemoteFixtures.Event(i, CanonicalEventTypes.AssistantTextGenerated, $$"""{"content":"Reply {{i}}"}"""))]);
        var chat = new ChatTabViewModel("remote", AgentOrigin.Remote,
            Observable.Return(new ChatSessionInfo("Ended", "Ended", "gemini", null, null, null, true, "", "s1")), Observable.Return<string[]?>(null),
            new ViewModels.AcceptingChatPromptInput(), new NoAttachmentUploader(),
            key => new RemoteTranscriptFeed(key, "gemini", access, server.Reader, lane, time, _ => { }, prefetch: 0),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
        var view = new ChatTabView { DataContext = chat };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        try {
            await WaitUntilAsync(() => chat.Items.Count == 300, what: "the loaded tail");
            var baseline = server.RequestCount;
            TurnPreview(true);
            await WaitUntilAsync(() => Navigator(view).IsVisible, what: "the remote turn navigator");
            var navigator = Navigator(view);
            MapButton(navigator).Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var list = TurnOutline(navigator);
            await Assert.That(server.RequestCount).IsEqualTo(baseline);
            await Assert.That(list.Items.OfType<ChatTurnOutlineEntry>().First(entry => entry.Destination is not null).Title).IsEqualTo("Partial turn");
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await WaitUntilAsync(() => chat.Items.Count == 350 && list.Items.OfType<ChatTurnOutlineEntry>().All(
                entry => entry.Command != ChatTurnOutlineEntry.Action.LoadEarlier), what: "the manually requested page and the outline update");
            await Assert.That(server.RequestCount).IsEqualTo(baseline + 1);

            access.OnNext(SessionAccessState.Denied);
            await WaitUntilAsync(() => !navigator.IsVisible, what: "the inaccessible outline to disappear");
            await Assert.That(list.ItemCount).IsEqualTo(0);

            var last = chat.Items[^1];
            access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => navigator.IsVisible, what: "the restored outline without new events");
            await Assert.That(chat.Items.Count).IsEqualTo(350);
            await Assert.That(chat.Items[^1]).IsSameReferenceAs(last);
            MapButton(navigator).Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(list.IsEffectivelyVisible).IsTrue();
        } finally {
            TurnPreview(false);
            window.Close();
            await chat.TeardownAsync();
        }
    });
}
