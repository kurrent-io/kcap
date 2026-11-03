using System.Runtime.CompilerServices;

namespace Capacitor.App.Platform.Windows;

/// Holds every navigation a page's WebView2 starts to the viewer's rule, in every frame, before its
/// request is sent. The WebView package listens to the top-level document only, and skips a URL
/// System.Uri refuses without cancelling it, so the guard handles the top level, every child frame
/// through FrameNavigationStarting, and each frame at any depth through its own NavigationStarting,
/// reached by FrameCreated. A new window is always refused here; the package raises the viewer's
/// prompt for one whose URL parses.
///
/// WebView2 reads Cancel once every handler has returned, and the package only ever sets it, so
/// the order in which the package's handlers and these run does not change the outcome.
public static class WebView2NavigationGuard {
    /// False when the guard could not be put in front of this web view, which must not load a page.
    /// failed runs if a frame appears that the guard cannot follow; the view must then stop showing
    /// the page.
    public static bool TryInstall(nint coreWebView2, Func<Uri, bool> cancel, Action failed) {
        if (!OperatingSystem.IsWindows() || coreWebView2 == 0) return false;
        var core4 = WebView2Com.QueryInterface(coreWebView2, WebView2Abi.ICoreWebView2_4);
        if (core4 == 0) return false;
        try {
            var topLevel = WebView2Callback.ForEvent([WebView2Abi.NavigationStartingEventHandler],
                (_, args) => OnNavigationStarting(args, cancel, topLevel: true));
            var navigation = WebView2Callback.ForEvent(
                [WebView2Abi.NavigationStartingEventHandler, WebView2Abi.FrameNavigationStartingEventHandler],
                (_, args) => OnNavigationStarting(args, cancel, topLevel: false));
            // It registers itself on each frame it is told of, so it reads its own pointer back.
            var frameCreated = new StrongBox<nint>();
            frameCreated.Value = WebView2Callback.ForEvent(
                [WebView2Abi.FrameCreatedEventHandler, WebView2Abi.FrameChildFrameCreatedEventHandler],
                (_, args) => Follow(args, navigation, frameCreated.Value, failed), navigation);
            var newWindow = WebView2Callback.ForEvent([WebView2Abi.NewWindowRequestedEventHandler], (_, args) => OnNewWindowRequested(args));
            try {
                return WebView2Com.AddHandler(coreWebView2, WebView2Abi.CoreAddNavigationStarting, topLevel, out _)
                    && WebView2Com.AddHandler(coreWebView2, WebView2Abi.CoreAddFrameNavigationStarting, navigation, out _)
                    && WebView2Com.AddHandler(coreWebView2, WebView2Abi.CoreAddNewWindowRequested, newWindow, out _)
                    && WebView2Com.AddHandler(core4, WebView2Abi.Core4AddFrameCreated, frameCreated.Value, out _);
            } finally {
                WebView2Com.Release(topLevel);
                WebView2Com.Release(newWindow);
                WebView2Com.Release(frameCreated.Value);
            }
        } finally {
            WebView2Com.Release(core4);
        }
    }

    /// The viewer's own NavigateToString, which WebView2 reports as a data: URL. Chromium refuses a
    /// top-level data: navigation that a page starts, so at the top level only the app makes one.
    /// Matched by prefix: System.Uri refuses a string as long as a large page's base64.
    public static bool IsHostLoad(string? url) => url?.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase) == true;

    /// True when a navigation to url must not go ahead: it does not parse, or the viewer's rule
    /// refuses it, or the rule failed. Only a top-level navigation can be the host load.
    public static bool Cancels(string? url, Func<Uri, bool> cancel, bool topLevel = false) {
        if (topLevel && IsHostLoad(url)) return false;
        if (!NavigationUrl.TryParse(url, out var uri)) return true;
        try {
            return cancel(uri);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: page navigation check failed: {ex.Message}");
            return true;
        }
    }

    /// Cancels before it reads anything, so a failure below leaves the navigation cancelled, and
    /// never lifts a cancel another handler set.
    static void OnNavigationStarting(nint args, Func<Uri, bool> cancel, bool topLevel) {
        var cancelledAlready = !WebView2Com.GetBool(args, WebView2Abi.NavigationStartingArgsGetCancel, out var cancelled) || cancelled;
        if (!WebView2Com.PutBool(args, WebView2Abi.NavigationStartingArgsPutCancel, true) || cancelledAlready) return;
        if (!Cancels(WebView2Com.GetString(args, WebView2Abi.NavigationStartingArgsGetUri), cancel, topLevel))
            WebView2Com.PutBool(args, WebView2Abi.NavigationStartingArgsPutCancel, false);
    }

    static void OnNewWindowRequested(nint args) =>
        WebView2Com.PutBool(args, WebView2Abi.NewWindowRequestedArgsPutHandled, true);

    /// Guards a new frame's own navigations and the frames it creates in turn. A runtime without
    /// either interface cannot be followed to that depth.
    static void Follow(nint args, nint navigation, nint frameCreated, Action failed) {
        var frame = WebView2Com.GetObject(args, WebView2Abi.FrameCreatedArgsGetFrame);
        var frame2 = WebView2Com.QueryInterface(frame, WebView2Abi.ICoreWebView2Frame2);
        var frame7 = WebView2Com.QueryInterface(frame, WebView2Abi.ICoreWebView2Frame7);
        try {
            var followed = frame2 != 0 && frame7 != 0
                && WebView2Com.AddHandler(frame2, WebView2Abi.Frame2AddNavigationStarting, navigation, out _)
                && WebView2Com.AddHandler(frame7, WebView2Abi.Frame7AddFrameCreated, frameCreated, out _);
            if (!followed) failed();
        } finally {
            WebView2Com.Release(frame7);
            WebView2Com.Release(frame2);
            WebView2Com.Release(frame);
        }
    }
}
