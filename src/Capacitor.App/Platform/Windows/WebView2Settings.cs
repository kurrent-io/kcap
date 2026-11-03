namespace Capacitor.App.Platform.Windows;

/// Takes the browser's own surfaces away from a page web view: DevTools, the context menu (whose
/// items open links and inspect the page), the status bar, and, where the runtime has them, the
/// browser accelerator keys. Editing keys keep working.
public static class WebView2Settings {
    /// False when a required setting did not take, and the web view must not load a page.
    public static bool TryHarden(nint coreWebView2) {
        if (!OperatingSystem.IsWindows() || coreWebView2 == 0) return false;
        var settings = WebView2Com.GetObject(coreWebView2, WebView2Abi.CoreGetSettings);
        if (settings == 0) return false;
        try {
            var hardened = WebView2Com.PutBool(settings, WebView2Abi.SettingsPutAreDevToolsEnabled, false)
                && WebView2Com.GetBool(settings, WebView2Abi.SettingsGetAreDevToolsEnabled, out var devTools) && !devTools
                && WebView2Com.PutBool(settings, WebView2Abi.SettingsPutAreDefaultContextMenusEnabled, false)
                && WebView2Com.PutBool(settings, WebView2Abi.SettingsPutIsStatusBarEnabled, false);
            if (!hardened) return false;
            var settings3 = WebView2Com.QueryInterface(settings, WebView2Abi.ICoreWebView2Settings3);
            if (settings3 != 0) {
                WebView2Com.PutBool(settings3, WebView2Abi.Settings3PutAreBrowserAcceleratorKeysEnabled, false);
                WebView2Com.Release(settings3);
            }
            return true;
        } finally {
            WebView2Com.Release(settings);
        }
    }
}
