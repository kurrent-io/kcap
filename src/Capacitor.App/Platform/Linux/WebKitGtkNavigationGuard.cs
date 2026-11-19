using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Avalonia.X11.Interop;
using static Capacitor.App.Platform.Linux.WebKitGtkInterop;

namespace Capacitor.App.Platform.Linux;

/// Holds every navigation a page's WebKitGTK view starts, in every frame, to the viewer's rule before
/// its request is sent. The WebView package's decide-policy handler returns "handled" without deciding,
/// which WebKitGTK treats as use, and lets a URL System.Uri refuses through: the guard blocks it and
/// decides each navigation itself. The rule runs on the UI thread, so the decision is held and made back
/// on the glib thread; until then the navigation waits, and one never decided stays stopped.
public static unsafe class WebKitGtkNavigationGuard {
    sealed record Rule(Func<Uri, bool> Cancel, Action<Uri> NewWindow);

    /// False when the guard is not in front of this web view, which must not load a page. Runs on the
    /// glib thread; cancel and newWindow are called on the UI thread.
    public static bool TryInstall(nint webView, Func<Uri, bool> cancel, Action<Uri> newWindow) {
        if (!OperatingSystem.IsLinux() || webView == 0) return false;
        var signal = SignalLookup("decide-policy", WebViewGetType());
        if (signal == 0 || SignalHandlersBlockMatched(webView, SignalMatchId, signal, 0, 0, 0, 0) == 0) return false;
        var rule = GCHandle.Alloc(new Rule(cancel, newWindow));
        delegate* unmanaged[Cdecl]<nint, nint, int, nint, int> decide = &Decide;
        delegate* unmanaged[Cdecl]<nint, nint, void> release = &Release;
        if (SignalConnectData(webView, "decide-policy", (nint)decide, GCHandle.ToIntPtr(rule), (nint)release, 0) != 0) return true;
        rule.Free();
        return false;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int Decide(nint webView, nint decision, int type, nint data) {
        if (type is not (PolicyNavigationAction or PolicyNewWindowAction)) return 0;
        try {
            if (GCHandle.FromIntPtr(data).Target is not Rule rule || !NavigationUrl.TryParse(RequestUrl(decision), out var uri)) {
                PolicyDecisionIgnore(decision);
                return 1;
            }
            if (type == PolicyNewWindowAction) {
                PolicyDecisionIgnore(decision);
                Dispatcher.UIThread.Post(() => Run(() => rule.NewWindow(uri)));
                return 1;
            }
            ObjectRef(decision);
            Dispatcher.UIThread.Post(() => {
                var cancels = true;
                Run(() => cancels = rule.Cancel(uri));
                _ = GtkInteropHelper.RunOnGlibThread(() => {
                    if (cancels) PolicyDecisionIgnore(decision);
                    else PolicyDecisionUse(decision);
                    ObjectUnref(decision);
                    return 0;
                });
            });
            return 1;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: page navigation check failed: {ex.Message}");
            PolicyDecisionIgnore(decision);
            return 1;
        }
    }

    static void Run(Action action) {
        try { action(); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: page navigation check failed: {ex.Message}"); }
    }

    static string? RequestUrl(nint decision) {
        var action = NavigationPolicyDecisionGetNavigationAction(decision);
        var request = action == 0 ? 0 : NavigationActionGetRequest(action);
        var uri = request == 0 ? 0 : UriRequestGetUri(request);
        return uri == 0 ? null : Marshal.PtrToStringUTF8(uri);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Release(nint data, nint closure) => GCHandle.FromIntPtr(data).Free();
}
