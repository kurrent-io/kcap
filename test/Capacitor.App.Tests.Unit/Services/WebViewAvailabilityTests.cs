using Avalonia.Platform;
using Capacitor.App.Services;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit.Services;

/// Pages render in-app on macOS, and on Windows where the WebView2 runtime is installed; Linux
/// always opens them in the browser.
public class WebViewAvailabilityTests {
    [Test, RunOn(OS.MacOs)]
    public async Task Webview_is_offered_on_macos() {
        await Assert.That(new WebViewAvailability().IsAvailable).IsTrue();
    }

    [Test, RunOn(OS.Windows)]
    public async Task Webview_is_offered_on_windows_exactly_when_webview2_is_installed() {
        var installed = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2) is { IsSupported: true, IsInstalled: true };
        await Assert.That(new WebViewAvailability().IsAvailable).IsEqualTo(installed);
    }

    [Test, RunOn(OS.Linux)]
    public async Task Webview_is_never_offered_on_linux() {
        await Assert.That(new WebViewAvailability().IsAvailable).IsFalse();
    }
}
