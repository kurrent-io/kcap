using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.Views;

/// The Pages tab view on its own: a selector over the pages and documents above the one that is open.
public class ArtefactsViewSmokeTests {
    const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    static IEnumerable<string> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "");

    /// Opening a page swaps the reader's prompt for the page viewer at the tab's full width, under a bar naming
    /// it. A page that is not found never reaches Ready, so no NativeWebView is built under the headless platform.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_opened_page_shows_the_viewer_in_the_right_pane() => RunOnUiAsync(async () => {
        var page = new ArtefactDto("01ec", "Release checklist", "u1", "org", 3, DateTimeOffset.UtcNow, true, null, null, "https://kurrent.kcap.ai/pages/01ec");
        var source = new FakePlanArtifactSource();
        source.EnqueuePages(new SessionPagesRead(SessionPlansReadKind.Ready, [page]));
        var vm = new ArtefactsTabViewModel(source, new PlanActivity(), TimeProvider.System, opener: new RecordingOpener(),
            pageContent: new FakePageContentSource(), webView: new FixedWebViewAvailability(true), inAppPages: () => true);
        var window = new Window { Content = new ArtefactsView { DataContext = vm }, Width = 900, Height = 600 };
        window.Show();
        vm.SwitchSession(Session, "/repo");
        await (vm.PendingPagesForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        await Assert.That(Texts(window)).Contains("Select a page or document to read it.");

        await vm.OpenPageCommand.Execute(vm.Pages[0]);
        await (vm.PendingPageLoadForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        await Assert.That(window.GetVisualDescendants().OfType<PageViewer>().Count()).IsEqualTo(1);
        await Assert.That(Texts(window)).Contains("This page is gone, or you can't see it.");
        await Assert.That(Texts(window).Any(t => t == "Select a page or document to read it.")).IsFalse();
        await Assert.That(window.GetVisualDescendants().OfType<NativeWebView>().Any()).IsFalse();
        await Assert.That(Texts(window)).Contains("Release checklist");
        await Assert.That(Find<Button>(window, "OpenInBrowser").IsEffectivelyVisible).IsTrue();
        await Assert.That(Find<Button>(window, "ItemSelector").IsEffectivelyVisible).IsFalse();
        var viewer = window.GetVisualDescendants().OfType<PageViewer>().Single();
        await Assert.That(viewer.Bounds.Width).IsEqualTo(window.Bounds.Width);
        window.Close();
        await vm.TeardownAsync();
    });

    /// With two pages the bar offers the selector, the arrows and the position, and choosing a page from the
    /// selector's menu opens it and closes the menu.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_selector_menu_opens_the_chosen_page_and_closes() => RunOnUiAsync(async () => {
        var now = DateTimeOffset.UtcNow;
        var source = new FakePlanArtifactSource();
        source.EnqueuePages(new SessionPagesRead(SessionPlansReadKind.Ready, [
            new ArtefactDto("01ec", "Release checklist", "u1", "org", 1, now, true, null, null, "https://kurrent.kcap.ai/pages/01ec"),
            new ArtefactDto("02fd", "Rollout notes", "u1", "org", 1, now, true, null, null, "https://kurrent.kcap.ai/pages/02fd"),
        ]));
        var vm = new ArtefactsTabViewModel(source, new PlanActivity(), TimeProvider.System, opener: new RecordingOpener(),
            pageContent: new FakePageContentSource(), webView: new FixedWebViewAvailability(true), inAppPages: () => true);
        var window = new Window { Content = new ArtefactsView { DataContext = vm }, Width = 900, Height = 600 };
        window.Show();
        vm.SwitchSession(Session, "/repo");
        await (vm.PendingPagesForTesting ?? Task.CompletedTask);
        await vm.OpenPageCommand.Execute(vm.Pages[0]);
        await (vm.PendingPageLoadForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var selector = Find<Button>(window, "ItemSelector");
        await Assert.That(selector.IsEffectivelyVisible).IsTrue();
        await Assert.That(Find<Button>(window, "NextItem").IsEffectivelyVisible).IsTrue();
        await Assert.That(Find<TextBlock>(window, "ItemPosition").Text).IsEqualTo("1 of 2");

        var menu = (Flyout)selector.Flyout!;
        menu.ShowAt(selector);
        Dispatcher.UIThread.RunJobs();
        await Assert.That(menu.IsOpen).IsTrue();
        var row = ((Control)menu.Content!).GetVisualDescendants().OfType<Button>().Single(b => Equals(b.CommandParameter, vm.Pages[1]));
        MainWindowSmokeTests.ClickFlyoutButton(row);
        await (vm.PendingPageLoadForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();

        await Assert.That(vm.PageViewer!.ArtefactId).IsEqualTo("02fd");
        await Assert.That(menu.IsOpen).IsFalse();
        window.Close();
        await vm.TeardownAsync();
    });

    /// The in-app switch raises nothing when it flips, so focusing an arrow re-asks it: a page that would now
    /// open in the browser stops being a step before the keyboard can press it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Focusing_an_arrow_rereads_the_in_app_switch() => RunOnUiAsync(async () => {
        var now = DateTimeOffset.UtcNow;
        var inApp = true;
        var source = new FakePlanArtifactSource();
        source.EnqueuePages(new SessionPagesRead(SessionPlansReadKind.Ready, [
            new ArtefactDto("01ec", "Release checklist", "u1", "org", 1, now, true, null, null, "https://kurrent.kcap.ai/pages/01ec"),
            new ArtefactDto("02fd", "Rollout notes", "u1", "org", 1, now, true, null, null, "https://kurrent.kcap.ai/pages/02fd"),
        ]));
        var vm = new ArtefactsTabViewModel(source, new PlanActivity(), TimeProvider.System, opener: new RecordingOpener(),
            pageContent: new FakePageContentSource(), webView: new FixedWebViewAvailability(true), inAppPages: () => inApp);
        var window = new Window { Content = new ArtefactsView { DataContext = vm }, Width = 900, Height = 600 };
        window.Show();
        vm.SwitchSession(Session, "/repo");
        await (vm.PendingPagesForTesting ?? Task.CompletedTask);
        await vm.OpenPageCommand.Execute(vm.Pages[0]);
        await (vm.PendingPageLoadForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var next = Find<Button>(window, "NextItem");
        await Assert.That(next.IsEffectivelyEnabled).IsTrue();

        inApp = false;
        next.Focus();
        Dispatcher.UIThread.RunJobs();

        await Assert.That(next.IsEffectivelyEnabled).IsFalse();
        window.Close();
        await vm.TeardownAsync();
    });

    /// A switch turned on elsewhere enables an arrow that was off once the window is active again, with no
    /// pointer or focus inside the bar: the flip is made in Settings or a terminal, which take the focus.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Reactivating_the_window_rereads_the_in_app_switch() => RunOnUiAsync(async () => {
        var now = DateTimeOffset.UtcNow;
        var inApp = false;
        var source = new FakePlanArtifactSource();
        source.Enqueue(new PlanArtifactsRead(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto { Artifacts = [new PlanArtifactDto {
            ArtifactId = "d1", Kind = "plan", Title = "docs/p.md", Source = "declared", SessionId = Session, Path = "docs/p.md", Content = "# p",
            ContentState = "ok", IsComplete = true, IsConfirmed = true, ContentHash = "h1", Version = 1, DiscoveredAt = now,
            Confidence = "high", Reason = "declared",
        }] }));
        source.EnqueuePages(new SessionPagesRead(SessionPlansReadKind.Ready, [
            new ArtefactDto("01ec", "Release checklist", "u1", "org", 1, now, true, null, null, "https://kurrent.kcap.ai/pages/01ec"),
        ]));
        var vm = new ArtefactsTabViewModel(source, new PlanActivity(), TimeProvider.System, opener: new RecordingOpener(),
            pageContent: new FakePageContentSource(), webView: new FixedWebViewAvailability(true), inAppPages: () => inApp);
        var window = new Window { Content = new ArtefactsView { DataContext = vm }, Width = 900, Height = 600 };
        var elsewhere = new Window { Width = 200, Height = 200 };
        window.Show();
        vm.SwitchSession(Session, "/repo");
        await Task.WhenAll(vm.PendingReadForTesting ?? Task.CompletedTask, vm.PendingPagesForTesting ?? Task.CompletedTask);
        await vm.SelectCommand.Execute(vm.Documents[0]);
        Dispatcher.UIThread.RunJobs();
        var previous = Find<Button>(window, "PreviousItem");
        await Assert.That(previous.IsEffectivelyEnabled).IsFalse();

        elsewhere.Show();
        elsewhere.Activate();
        inApp = true;
        window.Activate();
        Dispatcher.UIThread.RunJobs();

        await Assert.That(previous.IsEffectivelyEnabled).IsTrue();
        elsewhere.Close();
        window.Close();
        await vm.TeardownAsync();
    });
}
