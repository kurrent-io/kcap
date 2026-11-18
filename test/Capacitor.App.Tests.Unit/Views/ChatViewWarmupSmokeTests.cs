using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Views;

[NotInParallel("AvaloniaSession")]
public class ChatViewWarmupSmokeTests {
    /// The warm-up chat is built inside the window, rows and all, then leaves it: no view stays in
    /// the tree and focus is where it was.
    [Test]
    public Task The_warm_up_chat_renders_its_rows_in_the_window_and_leaves_no_trace() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var focused = window.FocusManager?.GetFocusedElement();
            var added = new List<ChatTabView>();
            var rows = new HashSet<Type>();
            window.WarmupHost.Children.CollectionChanged += (_, e) => {
                foreach (var view in e.NewItems?.OfType<ChatTabView>() ?? []) {
                    added.Add(view);
                    view.LayoutUpdated += (_, _) => rows.UnionWith(view.FindControl<ItemsControl>("ChatItems")!
                        .GetRealizedContainers().Select(c => c.DataContext?.GetType()).OfType<Type>());
                }
            };

            var warmup = new ChatViewWarmup(new FakePermissionService(), new RecordingOpener(), TimeProvider.System);
            warmup.Schedule(window);
            await warmup.RunForTesting!;

            var view = added.Single();
            await Assert.That(rows).Contains(typeof(UserTurnItem));
            await Assert.That(rows).Contains(typeof(AssistantTextItem));
            await Assert.That(TopLevel.GetTopLevel(view)).IsNull();
            await Assert.That(window.GetVisualDescendants().OfType<ChatTabView>()).IsEmpty();
            await Assert.That(window.FocusManager?.GetFocusedElement()).IsSameReferenceAs(focused);
        } finally {
            window.Close();
        }
    });
}
