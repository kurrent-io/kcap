using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Capacitor.App.Platform.Linux.WebKitGtkInterop;

namespace Capacitor.App.Platform.Linux;

/// Takes WebRTC, DevTools and the context menu away from a page's WebKitGTK view. A page's content
/// policy cannot stop WebRTC: its ICE traffic is not a fetch, so connect-src never sees it. Runs on
/// the glib thread.
public static unsafe class WebKitGtkSettings {
    /// False when a setting did not take, and the web view must not load a page.
    public static bool TryHarden(nint webView) {
        if (!OperatingSystem.IsLinux() || webView == 0) return false;
        var settings = WebViewGetSettings(webView);
        if (settings == 0) return false;
        SettingsSetEnableWebRtc(settings, 0);
        SettingsSetEnableDeveloperExtras(settings, 0);
        delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, int> noMenu = &NoContextMenu;
        return SettingsGetEnableWebRtc(settings) == 0
            && SettingsGetEnableDeveloperExtras(settings) == 0
            && SignalConnectData(webView, "context-menu", (nint)noMenu, 0, 0, 0) != 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int NoContextMenu(nint webView, nint menu, nint gdkEvent, nint hitTest, nint data) => 1;
}
