using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Capacitor.App.Platform.Linux;
using Capacitor.App.Platform.Mac;
using Capacitor.App.Platform.Windows;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Hosts a ready page in a NativeWebView. The WebView exists only while the page is Ready, which
/// the view model reaches only where IWebViewAvailability allows it, so a runtime without a usable
/// WebView never constructs one. A page loads only into a WKWebView, WebView2 or WebKitGTK view whose WebRTC is
/// shown to be off and whose navigations, in every frame, pass the platform's guard; anything less
/// falls back to the browser. Every handler the WebView raises catches what it throws: they run
/// from native callbacks, where an escaping exception ends the process.
public partial class PageViewer : UserControl {
    PageViewerViewModel? _vm;
    NativeWebView? _webView;
    PageBridge? _bridge;
    bool _attached;
    bool _webViewSafe;
    bool _rendered;

    public PageViewer() {
        InitializeComponent();
        DataContextChanged += (_, _) => {
            if (_attached) Attach(DataContext as PageViewerViewModel);
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Attach(DataContext as PageViewerViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = null;
        ReplaceBridge(null);
    }

    void Attach(PageViewerViewModel? vm) {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        Show();
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName is nameof(PageViewerViewModel.Html) or nameof(PageViewerViewModel.State)) Show();
    }

    void Show() {
        if (_vm is { State: PageViewerState.Ready, Html: { } html } vm) {
            var webView = EnsureWebView();
            // Until the native view exists and WebRTC is off; AdapterCreated shows the page then.
            if (!_webViewSafe) {
                ReplaceBridge(null);
                return;
            }
            ReplaceBridge(vm.CreateBridge(json => DeliverAsync(webView, json)));
            ShowRendered(false);
            webView.NavigateToString(html, PageViewerViewModel.HostBase);
            return;
        }
        ReplaceBridge(null);
        WebHost.Child = null;
        _webView = null;
        _webViewSafe = false;
        ShowRendered(false);
    }

    /// The native view draws over anything Avalonia puts on top of it, so it stays hidden behind the
    /// spinner until its first load has painted.
    void ShowRendered(bool rendered) {
        _rendered = rendered;
        var ready = _vm?.State == PageViewerState.Ready;
        PageLoading.IsVisible = _vm?.State == PageViewerState.Loading || (ready && !rendered);
        WebHost.IsVisible = ready && rendered;
    }

    void ReplaceBridge(PageBridge? next) {
        var old = _bridge;
        _bridge = next;
        if (old is not null) _ = old.DisposeAsync().AsTask();
        next?.Start();
    }

    static Task DeliverAsync(NativeWebView webView, string json) =>
        Dispatcher.UIThread.InvokeAsync(() => webView.InvokeScript($"window.kcapDeliver && window.kcapDeliver({json})"));

    NativeWebView EnsureWebView() {
        if (_webView is not null) return _webView;
        var webView = new NativeWebView();
        // WebView2's adapter raises NavigationStarted for the top-level document only.
        var webView2 = false;
        webView.EnvironmentRequested += (_, e) => {
            try {
                if (e is AppleWKWebViewEnvironmentRequestedEventArgs apple) apple.NonPersistentDataStore = true;
                else if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
                    WebView2PageEnvironment.Configure(windows, Environment.GetEnvironmentVariable("LOCALAPPDATA"));
                else if (e is GtkWebViewEnvironmentRequestedEventArgs gtk) gtk.EphemeralDataManager = true;
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page web view setup failed: {ex.Message}");
            }
        };
        webView.AdapterCreated += (_, e) => {
            try {
                if (!ReferenceEquals(webView, _webView)) return;
                switch (e.TryGetPlatformHandle()) {
                    case IAppleWKWebViewPlatformHandle apple:
                        SecureWebKit(apple);
                        break;
                    case IWindowsWebView2PlatformHandle windows:
                        webView2 = true;
                        _ = SecureWebView2Async(webView, windows);
                        break;
                    case IGtkWebViewPlatformHandle gtk:
                        _ = SecureWebKitGtkAsync(webView, gtk.WebKitWebView);
                        break;
                    default:
                        Console.Error.WriteLine("kcap: the page web view is not a WKWebView, WebView2 or WebKitGTK view; pages open in the browser");
                        _vm?.WebViewUnusable();
                        break;
                }
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page web view setup failed: {ex.Message}");
                _vm?.WebViewUnusable();
            }
        };
        webView.AdapterDestroyed += (_, _) => {
            try {
                if (ReferenceEquals(webView, _webView)) {
                    _webViewSafe = false;
                    ReplaceBridge(null);
                }
                // A new native view replays the last load before AdapterCreated: make that a blank one.
                webView.NavigateToString("", PageViewerViewModel.HostBase);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page web view teardown failed: {ex.Message}");
            }
        };
        webView.NavigationStarted += (_, e) => {
            e.Cancel = true;
            try {
                e.Cancel = !(webView2 && WebView2NavigationGuard.IsHostLoad(e.Request?.OriginalString))
                    && (_vm?.CancelNavigation(e.Request) ?? true);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page navigation check failed: {ex.Message}");
            }
        };
        webView.NavigationCompleted += (_, _) => {
            try {
                if (ReferenceEquals(webView, _webView) && _webViewSafe && !_rendered) ShowRendered(true);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page load failed: {ex.Message}");
            }
        };
        webView.NewWindowRequested += (_, e) => {
            e.Handled = true;
            try {
                _vm?.OpenNewWindow(e.Request);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page new-window request failed: {ex.Message}");
            }
        };
        webView.WebMessageReceived += (_, e) => {
            try {
                if (_bridge is { } bridge && ReferenceEquals(webView, _webView)) _ = bridge.OnNativeMessageAsync(e.Body ?? "");
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: page message failed: {ex.Message}");
            }
        };
        _webView = webView;
        WebHost.Child = webView;
        return webView;
    }

    void SecureWebKit(IAppleWKWebViewPlatformHandle apple) {
        if (!WebKitPeerConnection.TryDisable(apple.WKWebView)) {
            Console.Error.WriteLine("kcap: could not turn WebRTC off in the page web view; pages open in the browser");
            _vm?.WebViewUnusable();
        } else if (!WebKitNavigationGuard.TryInstall(apple.WKWebView)) {
            Console.Error.WriteLine("kcap: could not guard the page web view's navigations; pages open in the browser");
            _vm?.WebViewUnusable();
        } else {
            _webViewSafe = true;
            Show();
        }
    }

    /// The handlers WebView2 keeps hold a weak reference only: the runtime may outlive this view.
    async Task SecureWebView2Async(NativeWebView webView, IWindowsWebView2PlatformHandle windows) {
        var self = new WeakReference<PageViewer>(this);
        var view = new WeakReference<NativeWebView>(webView);
        var lostFrame = false;
        nint core = 0;
        try {
            // The package's getter does not say whether it hands over a reference, so this takes its own.
            core = windows.CoreWebView2;
            WebView2Com.AddRef(core);
            string? problem =
                core == 0 ? "the page web view has no WebView2 core"
                : !WebView2Settings.TryHarden(core) ? "could not lock the page web view's settings"
                : !WebView2NavigationGuard.TryInstall(core, uri => Cancels(self, uri), () => {
                    lostFrame = true;
                    Dispatcher.UIThread.Post(() => Unusable(self, view));
                }) ? "could not guard the page web view's navigations"
                : !await WebView2PeerConnection.TryDisableAsync(core) ? "could not turn WebRTC off in the page web view"
                : null;
            // The WebRTC probe's nested frames are the guard's first frames to follow.
            if (problem is null && lostFrame) problem = "could not guard the page web view's frames";
            if (!ReferenceEquals(webView, _webView)) return;
            if (problem is not null) {
                Console.Error.WriteLine($"kcap: {problem}; pages open in the browser");
                _vm?.WebViewUnusable();
                return;
            }
            _webViewSafe = true;
            Show();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: page web view setup failed: {ex.Message}");
            if (ReferenceEquals(webView, _webView)) _vm?.WebViewUnusable();
        } finally {
            WebView2Com.Release(core);
        }
    }

    /// The guard holds a weak reference only: the native view may outlive this one.
    async Task SecureWebKitGtkAsync(NativeWebView webView, nint webKitWebView) {
        var self = new WeakReference<PageViewer>(this);
        try {
            var problem = await Avalonia.X11.Interop.GtkInteropHelper.RunOnGlibThread(() =>
                !WebKitGtkSettings.TryHarden(webKitWebView) ? "could not lock the page web view's settings"
                : !WebKitGtkNavigationGuard.TryInstall(webKitWebView, uri => Cancels(self, uri), uri => OpenNewWindow(self, uri))
                    ? "could not guard the page web view's navigations"
                : null);
            if (!ReferenceEquals(webView, _webView)) return;
            if (problem is not null) {
                Console.Error.WriteLine($"kcap: {problem}; pages open in the browser");
                _vm?.WebViewUnusable();
                return;
            }
            _webViewSafe = true;
            Show();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: page web view setup failed: {ex.Message}");
            if (ReferenceEquals(webView, _webView)) _vm?.WebViewUnusable();
        }
    }

    static void OpenNewWindow(WeakReference<PageViewer> self, Uri request) {
        if (self.TryGetTarget(out var viewer)) viewer._vm?.OpenNewWindow(request);
    }

    static bool Cancels(WeakReference<PageViewer> self, Uri request) =>
        !self.TryGetTarget(out var viewer) || viewer._vm is not { } vm || vm.CancelNavigation(request);

    static void Unusable(WeakReference<PageViewer> self, WeakReference<NativeWebView> view) {
        try {
            if (self.TryGetTarget(out var viewer) && view.TryGetTarget(out var webView) && ReferenceEquals(webView, viewer._webView))
                viewer._vm?.WebViewUnusable();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: page web view teardown failed: {ex.Message}");
        }
    }
}
