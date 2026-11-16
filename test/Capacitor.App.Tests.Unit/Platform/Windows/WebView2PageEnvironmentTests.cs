using Capacitor.App.Platform.Windows;

namespace Capacitor.App.Tests.Unit.Platform.Windows;

/// The page web view's data stays under the app's own %LOCALAPPDATA% folder, and an unusable
/// %LOCALAPPDATA% leaves WebView2 its default rather than a folder relative to the working directory.
public class WebView2PageEnvironmentTests {
    [Test]
    public async Task The_data_folder_is_under_the_apps_local_app_data_folder() {
        var root = Path.GetFullPath("app-data");
        await Assert.That(WebView2PageEnvironment.UserDataFolder(root)).IsEqualTo(Path.Combine(root, "kcap", "PageWebView2"));
    }

    /// Every connection, loopback too, goes to a proxy nothing listens on, beside the WebRTC policy.
    [Test]
    public async Task The_browser_arguments_send_every_connection_to_a_dead_proxy() {
        await Assert.That(WebView2PageEnvironment.BrowserArguments).Contains("--proxy-server=http://127.0.0.1:1");
        await Assert.That(WebView2PageEnvironment.BrowserArguments).Contains("--proxy-bypass-list=<-loopback>");
        await Assert.That(WebView2PageEnvironment.BrowserArguments).Contains(WebView2PeerConnection.BrowserArguments);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("relative")]
    public async Task An_unusable_local_app_data_leaves_the_default(string? localAppData) {
        await Assert.That(WebView2PageEnvironment.UserDataFolder(localAppData)).IsNull();
    }
}
