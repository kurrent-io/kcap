using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Views;

[NotInParallel("AvaloniaSession")]
public class ChatViewWarmupSmokeTests {
    static (MainWindow Window, ChatViewWarmup Warmup) Open() {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, new ChatViewWarmup(new FakePermissionService(), new RecordingOpener(), TimeProvider.System));
    }

    /// The warm-up builds a session view inside the window, tool call rows included, then leaves it:
    /// nothing stays in the tree and focus is where it was.
    [Test]
    public Task The_warm_up_renders_its_rows_in_the_window_and_leaves_no_trace() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, warmup) = Open();
        try {
            var focused = window.FocusManager?.GetFocusedElement();
            var added = new List<RemoteSessionView>();
            var rows = new HashSet<Type>();
            var toolRows = 0;
            window.ContentPane.Children.CollectionChanged += (_, e) => {
                foreach (var view in e.NewItems?.OfType<RemoteSessionView>() ?? []) {
                    added.Add(view);
                    view.LayoutUpdated += (_, _) => {
                        rows.UnionWith(view.ChatHost.FindControl<ItemsControl>("ChatItems")!
                            .GetRealizedContainers().Select(c => c.DataContext?.GetType()).OfType<Type>());
                        toolRows = Math.Max(toolRows, view.GetVisualDescendants().OfType<Control>().Count(c => c.Name == "ToolCallRow"));
                    };
                }
            };

            warmup.Schedule(window);
            await warmup.RunForTesting!;

            var view = added.Single();
            await Assert.That(rows).Contains(typeof(UserTurnItem));
            await Assert.That(rows).Contains(typeof(AssistantTextItem));
            await Assert.That(toolRows).IsGreaterThanOrEqualTo(3);
            await Assert.That(TopLevel.GetTopLevel(view)).IsNull();
            await Assert.That(window.GetVisualDescendants().OfType<ChatTabView>()).IsEmpty();
            await Assert.That(window.FocusManager?.GetFocusedElement()).IsSameReferenceAs(focused);
        } finally {
            window.Close();
        }
    });

    /// Find and Tab pressed while the warm-up is in the tree must not reach it: focus inside would
    /// be dropped when the view leaves, under whatever the user was typing.
    [Test]
    public Task Find_and_tab_pass_the_warm_up_by() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, warmup) = Open();
        try {
            var reached = new List<string>();
            var pressed = false;
            var find = ShortcutLabels.UsesCommandKey ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.ContentPane.Children.CollectionChanged += (_, e) => {
                foreach (var view in e.NewItems?.OfType<RemoteSessionView>() ?? []) {
                    view.LayoutUpdated += (_, _) => {
                        if (pressed || view.ChatHost.DataContext is not ChatTabViewModel { Items.Count: > 0 }) return;
                        pressed = true;
                        window.KeyPressQwerty(PhysicalKey.F, find);
                        for (var i = 0; i < 40; i++) {
                            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                            if (window.FocusManager?.GetFocusedElement() is Visual f && f.GetSelfAndVisualAncestors().Contains(view))
                                reached.Add(f.GetType().Name);
                        }
                        if (view.ChatHost.DataContext is ChatTabViewModel { Find.IsOpen: true }) reached.Add("find bar");
                    };
                }
            };

            warmup.Schedule(window);
            await warmup.RunForTesting!;

            await Assert.That(pressed).IsTrue();
            await Assert.That(reached).IsEmpty();
        } finally {
            window.Close();
        }
    });
}
