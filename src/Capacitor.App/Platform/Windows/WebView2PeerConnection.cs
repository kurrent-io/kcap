namespace Capacitor.App.Platform.Windows;

/// Takes WebRTC away from a page's WebView2. A page's content policy cannot stop it: ICE traffic
/// is not a fetch, so connect-src never sees it, and WebView2 has no setting that turns it off.
/// A script WebView2 runs at the creation of every document, in every frame, before any of the
/// document's own, deletes the constructors. The script is not trusted untested: before a page
/// loads, a probe document with nested frames is loaded and read back, and a view whose frames
/// still see a constructor must not load a page. The browser arguments, which nothing reads back,
/// only narrow ICE to proxied transports should a connection form anyway.
public static class WebView2PeerConnection {
    public const string BrowserArguments =
        "--force-webrtc-ip-handling-policy=disable_non_proxied_udp --webrtc-ip-handling-policy=disable_non_proxied_udp";

    const string Constructors = "['RTCPeerConnection','webkitRTCPeerConnection','RTCDataChannel']";

    const string DisableScript =
        "(function(){var n=" + Constructors + ";for(var i=0;i<n.length;i++){try{delete window[n[i]];}catch(e){}}})();";

    const string ProbeDocument =
        "<!doctype html><html><body><iframe srcdoc=\"<iframe srcdoc='<p>'></iframe>\"></iframe></body></html>";

    /// Reads the main document, its frame, that frame's frame, and a frame created on the spot, whose
    /// first document is the one a page could reach without any navigation.
    const string ProbeScript =
        "(function(){try{var n=" + Constructors + ",w=[window];" +
        "for(var i=0;i<frames.length;i++){w.push(frames[i]);for(var j=0;j<frames[i].frames.length;j++)w.push(frames[i].frames[j]);}" +
        "var f=document.createElement('iframe');document.body.appendChild(f);w.push(f.contentWindow);" +
        "var off=w.length===4;for(var k=0;k<w.length;k++)for(var m=0;m<n.length;m++)if(typeof w[k][n[m]]!=='undefined')off=false;" +
        "f.remove();return off?'off':'on';}catch(e){return 'error';}})()";

    const string ProbeOff = "\"off\"";

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// False when WebRTC could not be shown to be gone from every frame, and the web view must not
    /// load a page. Leaves the probe document loaded.
    public static async Task<bool> TryDisableAsync(nint coreWebView2) {
        if (!OperatingSystem.IsWindows() || coreWebView2 == 0) return false;
        try {
            if (!await AddScriptAsync(coreWebView2)) return Refused("the script that removes it was not accepted");
            if (!await NavigateToStringAsync(coreWebView2, ProbeDocument)) return Refused("its probe document did not load");
            var read = await ExecuteScriptAsync(coreWebView2, ProbeScript);
            return read == ProbeOff || Refused($"its probe read {read ?? "nothing"}");
        } catch (TimeoutException) {
            Console.Error.WriteLine("kcap: the page web view did not answer the WebRTC check in time");
            return false;
        }
    }

    static bool Refused(string why) {
        Console.Error.WriteLine($"kcap: WebRTC check in the page web view: {why}");
        return false;
    }

    static Task<bool> AddScriptAsync(nint core) {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = WebView2Callback.ForCompletion(WebView2Abi.AddScriptToExecuteOnDocumentCreatedCompletedHandler,
            (errorCode, _) => done.TrySetResult(errorCode >= 0));
        try {
            if (!WebView2Com.CallWithStringAndHandler(core, WebView2Abi.CoreAddScriptToExecuteOnDocumentCreated, DisableScript, handler))
                return Task.FromResult(false);
        } finally {
            WebView2Com.Release(handler);
        }
        return done.Task.WaitAsync(Timeout, TimeProvider.System);
    }

    static async Task<bool> NavigateToStringAsync(nint core, string html) {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = WebView2Callback.ForEvent([WebView2Abi.NavigationCompletedEventHandler],
            (_, args) => done.TrySetResult(WebView2Com.GetBool(args, WebView2Abi.NavigationCompletedArgsGetIsSuccess, out var success) && success));
        long token;
        try {
            if (!WebView2Com.AddHandler(core, WebView2Abi.CoreAddNavigationCompleted, handler, out token)) return false;
        } finally {
            WebView2Com.Release(handler);
        }
        try {
            return WebView2Com.CallWithString(core, WebView2Abi.CoreNavigateToString, html) && await done.Task.WaitAsync(Timeout, TimeProvider.System);
        } finally {
            WebView2Com.RemoveHandler(core, WebView2Abi.CoreRemoveNavigationCompleted, token);
        }
    }

    static Task<string?> ExecuteScriptAsync(nint core, string script) {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = WebView2Callback.ForCompletion(WebView2Abi.ExecuteScriptCompletedHandler,
            (errorCode, json) => done.TrySetResult(errorCode >= 0 ? json : null));
        try {
            if (!WebView2Com.CallWithStringAndHandler(core, WebView2Abi.CoreExecuteScript, script, handler))
                return Task.FromResult<string?>(null);
        } finally {
            WebView2Com.Release(handler);
        }
        return done.Task.WaitAsync(Timeout, TimeProvider.System);
    }
}
