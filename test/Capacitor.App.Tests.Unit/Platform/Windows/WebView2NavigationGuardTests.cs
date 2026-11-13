using Capacitor.App.Platform.Windows;

namespace Capacitor.App.Tests.Unit.Platform.Windows;

/// A frame's navigation goes ahead only when its URL parses and the viewer's rule lets it; every
/// other outcome, a failing rule included, cancels it.
public class WebView2NavigationGuardTests {
    [Test]
    [Arguments("mailto:%")]
    [Arguments("foo://a%zz/")]
    [Arguments(null)]
    public async Task A_url_system_uri_refuses_is_cancelled_without_asking_the_viewer(string? url) {
        var asked = false;
        await Assert.That(WebView2NavigationGuard.Cancels(url, _ => { asked = true; return false; })).IsTrue();
        await Assert.That(asked).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_url_that_parses_takes_the_viewers_answer(bool viewerCancels) {
        Uri? asked = null;
        var cancels = WebView2NavigationGuard.Cancels("about:srcdoc", uri => { asked = uri; return viewerCancels; });
        await Assert.That(cancels).IsEqualTo(viewerCancels);
        await Assert.That(asked?.OriginalString).IsEqualTo("about:srcdoc");
    }

    /// WebView2 reports the viewer's own NavigateToString as a data: URL, often longer than System.Uri
    /// accepts; it goes ahead at the top level only, and a frame's data: navigation still takes the rule.
    [Test]
    public async Task The_host_load_goes_ahead_at_the_top_level_only() {
        var hostLoad = "data:text/html;charset=utf-8;base64," + new string('A', 70_000);
        await Assert.That(WebView2NavigationGuard.Cancels(hostLoad, _ => true, topLevel: true)).IsFalse();
        await Assert.That(WebView2NavigationGuard.Cancels("data:text/html,<p>", _ => true, topLevel: false)).IsTrue();
        await Assert.That(WebView2NavigationGuard.Cancels("data:image/svg+xml,<svg/>", _ => true, topLevel: true)).IsTrue();
    }

    [Test]
    public async Task A_rule_that_throws_cancels() {
        await Assert.That(WebView2NavigationGuard.Cancels("https://example.test/", _ => throw new InvalidOperationException())).IsTrue();
    }

    [Test]
    public async Task Without_a_web_view_nothing_is_installed() {
        await Assert.That(WebView2NavigationGuard.TryInstall(0, _ => true, () => { })).IsFalse();
    }
}
