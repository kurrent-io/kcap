using System.Runtime.InteropServices;

namespace Capacitor.App.Platform.Linux;

/// The WebKitGTK and GObject calls the page web view's guards make. Every call must run on the glib
/// thread that owns the web view.
internal static partial class WebKitGtkInterop {
    // The 4.0 ABI is not looked for: a system without 4.1 fails the guards and opens pages in the browser.
    const string WebKit = "libwebkit2gtk-4.1.so.0";
    const string GObject = "libgobject-2.0.so.0";

    public const int PolicyNavigationAction = 0;
    public const int PolicyNewWindowAction = 1;
    public const uint SignalMatchId = 1;

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_get_type")]
    public static partial nuint WebViewGetType();

    [LibraryImport(WebKit, EntryPoint = "webkit_web_view_get_settings")]
    public static partial nint WebViewGetSettings(nint webView);

    [LibraryImport(WebKit, EntryPoint = "webkit_settings_set_enable_webrtc")]
    public static partial void SettingsSetEnableWebRtc(nint settings, int enabled);

    [LibraryImport(WebKit, EntryPoint = "webkit_settings_get_enable_webrtc")]
    public static partial int SettingsGetEnableWebRtc(nint settings);

    [LibraryImport(WebKit, EntryPoint = "webkit_settings_set_enable_developer_extras")]
    public static partial void SettingsSetEnableDeveloperExtras(nint settings, int enabled);

    [LibraryImport(WebKit, EntryPoint = "webkit_settings_get_enable_developer_extras")]
    public static partial int SettingsGetEnableDeveloperExtras(nint settings);

    [LibraryImport(WebKit, EntryPoint = "webkit_navigation_policy_decision_get_navigation_action")]
    public static partial nint NavigationPolicyDecisionGetNavigationAction(nint decision);

    [LibraryImport(WebKit, EntryPoint = "webkit_navigation_action_get_request")]
    public static partial nint NavigationActionGetRequest(nint action);

    [LibraryImport(WebKit, EntryPoint = "webkit_uri_request_get_uri")]
    public static partial nint UriRequestGetUri(nint request);

    [LibraryImport(WebKit, EntryPoint = "webkit_policy_decision_use")]
    public static partial void PolicyDecisionUse(nint decision);

    [LibraryImport(WebKit, EntryPoint = "webkit_policy_decision_ignore")]
    public static partial void PolicyDecisionIgnore(nint decision);

    [LibraryImport(GObject, EntryPoint = "g_signal_lookup", StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint SignalLookup(string name, nuint type);

    [LibraryImport(GObject, EntryPoint = "g_signal_handlers_block_matched")]
    public static partial uint SignalHandlersBlockMatched(nint instance, uint mask, uint signalId, uint detail, nint closure, nint func, nint data);

    [LibraryImport(GObject, EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint SignalConnectData(nint instance, string signal, nint handler, nint data, nint destroyData, int flags);

    [LibraryImport(GObject, EntryPoint = "g_object_ref")]
    public static partial nint ObjectRef(nint instance);

    [LibraryImport(GObject, EntryPoint = "g_object_unref")]
    public static partial void ObjectUnref(nint instance);
}
