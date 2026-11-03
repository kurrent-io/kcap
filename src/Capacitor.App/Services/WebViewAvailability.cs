using Avalonia.Platform;

namespace Capacitor.App.Services;

/// A page is hosted in-app only where the viewer can cancel a navigation in any frame before its
/// request is sent: WKWebView, whose policy decision covers subframes, and WebView2, through the
/// app's own frame handlers. This only says the runtime is there; each web view must still prove
/// its guard and WebRTC switch when it is created, or the page opens in the browser. WebKitGTK is
/// unproven, so Linux opens pages in the browser.
public sealed class WebViewAvailability : IWebViewAvailability {
    public bool IsAvailable { get; } =
        OperatingSystem.IsMacOS() ? WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WkWebView).IsSupported
        : OperatingSystem.IsWindows() && WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2) is { IsSupported: true, IsInstalled: true };
}
