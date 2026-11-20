using Avalonia;
using Avalonia.Automation.Peers;
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
        return (window, new ChatViewWarmup(new FakePermissionService(), new RecordingOpener(), TimeProvider.System, enabled: true));
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
                foreach (var view in e.NewItems?.OfType<WarmupHost>().Select(host => (RemoteSessionView)host.Child!) ?? []) {
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
                foreach (var view in e.NewItems?.OfType<WarmupHost>().Select(host => (RemoteSessionView)host.Child!) ?? []) {
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

    /// An accessibility client walks the window's peers, not its Tab order: the warm-up's controls
    /// must not be listed there. Focus that reaches inside by any other route goes back where it was.
    [Test]
    public Task Accessibility_clients_do_not_see_the_warm_up_and_focus_inside_returns() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, warmup) = Open();
        try {
            var typing = window.GetVisualDescendants().OfType<TextBox>().First(box => box.IsEffectivelyVisible);
            typing.Focus();
            var exposed = new List<string>();
            var walked = false;
            var stolen = false;
            window.ContentPane.Children.CollectionChanged += (_, e) => {
                foreach (var host in e.NewItems?.OfType<Control>() ?? []) {
                    host.LayoutUpdated += (_, _) => {
                        if (walked || host.GetVisualDescendants().OfType<ChatTabView>().FirstOrDefault()?.DataContext
                                is not ChatTabViewModel { Items.Count: > 0 }) return;
                        walked = true;
                        foreach (var peer in ControlView.Descendants(window))
                            if (peer is ControlAutomationPeer { Owner: var owner } && owner.GetSelfAndVisualAncestors().Contains(host))
                                exposed.Add(owner.GetType().Name);
                        stolen = host.GetVisualDescendants().OfType<TextBox>().First().Focus();
                    };
                }
            };

            warmup.Schedule(window);
            await warmup.RunForTesting!;

            await Assert.That(walked).IsTrue();
            await Assert.That(exposed).IsEmpty();
            await Assert.That(stolen).IsTrue();
            await Assert.That(window.FocusManager?.GetFocusedElement()).IsSameReferenceAs(typing);
        } finally {
            window.Close();
        }
    });

    /// A show before the rail has drawn its first session rows starts nothing; the rows starting it is what runs it.
    [Test]
    public Task The_warm_up_waits_for_the_rail_to_draw_its_first_rows() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, warmup) = Open();
        try {
            warmup.Shown(window);
            await Assert.That(warmup.RunForTesting is null).IsTrue();

            warmup.RowsPainted(window);
            await Assert.That(warmup.RunForTesting is null).IsFalse();
            await warmup.RunForTesting!;
        } finally {
            window.Close();
        }
    });

    /// A window hidden to the tray before the warm-up runs is warmed on its next show, not never.
    [Test]
    public Task A_warm_up_that_found_the_window_hidden_runs_on_the_next_show() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, warmup) = Open();
        try {
            var hosts = 0;
            window.ContentPane.Children.CollectionChanged += (_, e) => hosts += e.NewItems?.OfType<WarmupHost>().Count() ?? 0;

            warmup.Schedule(window);
            window.Hide();
            await warmup.RunForTesting!;
            await Assert.That(hosts).IsEqualTo(0);

            window.Show();
            warmup.Schedule(window);
            await warmup.RunForTesting!;
            await Assert.That(hosts).IsEqualTo(1);
        } finally {
            window.Close();
        }
    });

    [Test]
    public Task A_disabled_warm_up_never_runs() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, _) = Open();
        try {
            var warmup = new ChatViewWarmup(new FakePermissionService(), new RecordingOpener(), TimeProvider.System, enabled: false);
            warmup.Schedule(window);
            await Assert.That(warmup.RunForTesting is null).IsTrue();
        } finally {
            window.Close();
        }
    });
}
