using Capacitor.App.Platform;

namespace Capacitor.App.Tests.Unit.Platform;

/// The URLs a navigation guard cancels are exactly those System.Uri refuses, which a web engine
/// hands the WebView package from a page's link and which it turns into a throw.
public class NavigationUrlTests {
    [Test]
    [Arguments("foo://exa%25mple/")]
    [Arguments("foo://a%zz/")]
    [Arguments("mailto:%")]
    [Arguments("mailto:a%zz")]
    [Arguments("foo://x:99999/")]
    [Arguments(null)]
    public async Task A_url_system_uri_refuses_does_not_parse(string? url) {
        await Assert.That(NavigationUrl.Parses(url)).IsFalse();
        await Assert.That(NavigationUrl.TryParse(url, out var uri)).IsFalse();
        await Assert.That(uri).IsNull();
        if (url is not null) await Assert.That(() => new Uri(url)).Throws<UriFormatException>();
    }

    [Test]
    [Arguments("about:blank")]
    [Arguments("about:srcdoc")]
    [Arguments("https://example.com/a?b=c#d")]
    [Arguments("mailto:someone@example.com")]
    public async Task A_url_the_viewer_handles_parses(string url) {
        await Assert.That(NavigationUrl.TryParse(url, out var uri)).IsTrue();
        await Assert.That(uri!.OriginalString).IsEqualTo(url);
    }
}
