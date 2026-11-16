using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.Views;

/// The Artefacts view on its own: an opened page shows in the right pane, and the tab lists no pages.
public class ArtefactsViewSmokeTests {
    const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    static IEnumerable<string> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "");

    /// The right pane swaps the reader's prompt for the page viewer, and with no documents to list the viewer
    /// takes the whole width. A page that is not found never reaches Ready, so no NativeWebView is built under
    /// the headless platform.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_opened_page_shows_the_viewer_in_the_right_pane() => RunOnUiAsync(async () => {
        var page = new ArtefactDto("01ec", "Release checklist", "u1", "org", 3, DateTimeOffset.UtcNow, true, "https://kurrent.kcap.ai/pages/01ec");
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
        await Assert.That(Texts(window)).Contains("No documents yet.");
        await Assert.That(Texts(window).Any(t => t == "Release checklist")).IsFalse();

        await vm.OpenPageCommand.Execute(vm.Pages[0]);
        await (vm.PendingPageLoadForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        await Assert.That(window.GetVisualDescendants().OfType<PageViewer>().Count()).IsEqualTo(1);
        await Assert.That(Texts(window)).Contains("This page is gone, or you can't see it.");
        await Assert.That(Texts(window).Any(t => t == "No documents yet.")).IsFalse();
        await Assert.That(window.GetVisualDescendants().OfType<NativeWebView>().Any()).IsFalse();
        var viewer = window.GetVisualDescendants().OfType<PageViewer>().Single();
        await Assert.That(viewer.Bounds.Width).IsEqualTo(window.Bounds.Width);
        window.Close();
        await vm.TeardownAsync();
    });
}
