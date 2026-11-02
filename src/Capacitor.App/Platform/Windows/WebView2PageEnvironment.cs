using Avalonia.Platform;

namespace Capacitor.App.Platform.Windows;

/// The WebView2 environment a page web view starts in: InPrivate, so a page leaves no cookies or
/// storage behind, with DevTools and the OS account's sign-in off and the WebRTC policy set. Its
/// user data sits in a folder of its own beside the app's install, because WebView2's default is
/// beside the executable, which an update replaces, and because every environment sharing a data
/// folder must start with the same browser arguments.
public static class WebView2PageEnvironment {
    public static void Configure(WindowsWebView2EnvironmentRequestedEventArgs e, string? localAppData) {
        e.IsInPrivateModeEnabled = true;
        e.EnableDevTools = false;
        e.AllowSingleSignOnUsingOSPrimaryAccount = false;
        e.AdditionalBrowserArguments = WebView2PeerConnection.BrowserArguments;
        if (UserDataFolder(localAppData) is { } folder) e.UserDataFolder = folder;
    }

    /// Null when %LOCALAPPDATA% is unset or not rooted, which leaves WebView2 its default.
    public static string? UserDataFolder(string? localAppData) =>
        localAppData is { Length: > 0 } && Path.IsPathRooted(localAppData)
            ? Path.Combine(localAppData, "kcap", "PageWebView2")
            : null;
}
