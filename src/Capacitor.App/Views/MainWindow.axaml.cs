using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Lucide.Avalonia;
using ReactiveUI.Reactive;
using ReactiveUI.Avalonia.Reactive;

namespace Capacitor.App.Views;

// ReactiveWindow<T> ties ViewModel.Activator to THIS window's own Loaded/Unloaded lifecycle
// (AvaloniaActivationForViewFetcher) — no manual Activator.Activate() call is needed; Show()
// activates the VM's WhenActivated projections, Close() deactivates them.
public partial class MainWindow : ReactiveWindow<MainWindowViewModel> {
    /// Assigned by MainWindowCoordinator on every window it builds: returns true when
    /// the close must be intercepted — the coordinator hides the window and the close below is
    /// cancelled. Left null on a plainly-constructed window (tests), where a close is a real
    /// close.
    public Func<bool>? CloseInterceptor { get; set; }

    /// The onboarding flow shown in place of the rail and launcher. Wizard-first mode builds this
    /// window with no MainWindowViewModel, so the pane gets its own DataContext rather than the
    /// window's; clearing it hands the window over to the main surface.
    public OnboardingViewModel? Onboarding {
        get => OnboardingPane.DataContext as OnboardingViewModel;
        set {
            OnboardingPane.DataContext = value;
            OnboardingPane.IsVisible   = value is not null;
            SessionsSurface.IsVisible  = value is null;
            ApplyMinimumSize();
        }
    }

    public PaneLayout Panes { get; } = new();

    ColumnDefinition RailColumn => SessionsSurface.ColumnDefinitions[0];

    void OnRailSplitterDragDelta(object? sender, VectorEventArgs e) =>
        SessionRail.Opacity = PaneLayout.HidesAt(RailColumn.ActualWidth, PaneLayout.RailMinWidth) ? PaneLayout.HidingOpacity : 1;

    // A drag resizes the column live; Panes takes the width once, when it ends. Released below the
    // minimum, the rail hides and reopens at the minimum.
    void OnRailSplitterDragCompleted(object? sender, VectorEventArgs e) {
        SessionRail.Opacity = 1;
        var width = RailColumn.ActualWidth;
        var hides = PaneLayout.HidesAt(width, PaneLayout.RailMinWidth);
        Panes.RailWidth = hides ? PaneLayout.RailMinWidth : width;
        if (hides) Panes.RailCollapsed = true;
    }

    void OnRailToggleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleRail();

    void ToggleRail() => Panes.RailCollapsed = !Panes.RailCollapsed;

    void ApplyRail() {
        var collapsed = Panes.RailCollapsed;
        RailColumn.Width = new GridLength(collapsed ? 0 : Panes.RailWidth);
        SessionRail.IsVisible = !collapsed;
        RailSplitter.IsVisible = !collapsed;
        RailToggleIcon.Kind = collapsed ? LucideIconKind.PanelLeftOpen : LucideIconKind.PanelLeftClose;
        ApplyRailAttention();
        ApplyRailClearance();
    }

    void ApplyRailAttention() {
        var collapsed = Panes.RailCollapsed;
        var attention = ViewModel?.RailAttention ?? RailAttentionSummary.None;
        RailAttentionMark.IsVisible = collapsed && attention.HasAttention;
        var tip = $"{(collapsed ? "Show" : "Hide")} sidebar";
        var summary = collapsed && attention.HasAttention ? attention.Text : null;
        ToolTip.SetTip(RailToggleButton, summary is null
            ? (object)$"{tip} ({ShortcutLabels.ToggleSidebar})"
            : new TextBlock { Text = $"{summary}\n{tip} ({ShortcutLabels.ToggleSidebar})", MaxWidth = 360, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(RailToggleButton, summary is null ? tip : $"{tip}: {summary}");
    }

    void LimitRail() =>
        RailColumn.MaxWidth = PaneLayout.MaxWidth(SessionsSurface.Bounds.Width, PaneLayout.RailMinWidth, Panes.RailRestMinWidth);

    /// How far a header at the content's leading edge pads to clear the window controls; zero while
    /// the rail shows.
    public double RailLeading => Panes.RailCollapsed ? RailClearance.Leading(_scale) : 0;

    public event EventHandler? RailLeadingChanged;

    void ApplyRailClearance() {
        var origin = RailClearance.ButtonOrigin(_scale);
        WindowControls.Margin = new Thickness(origin.X, origin.Y, 0, 0);
        LauncherHeader.Margin = new Thickness(Math.Max(0, RailLeading - LauncherPane.Margin.Left), 0, 0, 0);
        RailLeadingChanged?.Invoke(this, EventArgs.Empty);
    }

    WindowNotificationManager? _notifications;
    IDisposable? _notifierSubscription;
    IAppNotifier? _notifier;

    PullRequestContextViewModel? _foregroundPullRequests;

    /// Assigned by App.BuildAndShowMainWindow — the SAME IAppNotifier instance
    /// AgentActionService pushes into, so the toast overlay and stderr mirroring are always in
    /// sync. Left null on a plainly-constructed window (tests that don't exercise toasts).
    public IAppNotifier? Notifier {
        get => _notifier;
        set {
            _notifier = value;
            _notifierSubscription?.Dispose();
            _notifierSubscription = value?.Messages
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ShowToast);
        }
    }

    UiScaleSetting? _contentScale;
    IDisposable? _contentScaleSubscription;

    /// Magnifies everything in the window. Assigned by App; left null on a plainly-constructed
    /// window, whose zoom shortcuts then do nothing.
    public UiScaleSetting? ContentScale {
        get => _contentScale;
        set {
            _contentScale = value;
            _contentScaleSubscription?.Dispose();
            _contentScaleSubscription = value?.Changes
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ApplyScale);
        }
    }

    double _scale = UiScale.Default;
    double _captionTop;

    void ApplyScale(double scale) {
        _scale = scale;
        ScaleHost.LayoutTransform = scale == UiScale.Default ? null : new ScaleTransform(scale, scale);
        ApplyMinimumSize();
        ApplyCaptionInset();
        ApplyRailClearance();
        if (Onboarding is not null) KeepOnboardingOnScreen();
    }

    // A larger minimum grows the frame from its top-left corner, which can push setup's footer off
    // the screen; the frame is pulled back inside the work area.
    void KeepOnboardingOnScreen() {
        if (!IsVisible || WindowState != WindowState.Normal || double.IsNaN(Width) || double.IsNaN(Height)) return;
        if ((Screens?.ScreenFromWindow(this) ?? Screens?.Primary) is not { } screen) return;
        var size = new Size(Math.Max(Width, MinWidth), Math.Max(Height, MinHeight));
        if (WindowRestore.FitOnScreen(Position, size, [new ScreenArea(screen.Bounds, screen.WorkingArea, screen.Scaling)]) is not { } fit) return;
        Position = fit.Position;
        Width = fit.Size.Width;
        Height = fit.Size.Height;
    }

    // A closed window has no platform window to ask a screen about. The minimum depends on the
    // screen at any scale, since it never exceeds the work area.
    void RefreshMinimumSize() {
        if (PlatformImpl is not null) ApplyMinimumSize();
    }

    // Bounded by the screen the window is on.
    void ApplyMinimumSize() {
        var area = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
        var available = area is null ? (Size?)null : area.WorkingArea.Size.ToSize(area.Scaling);
        var minimum = Onboarding is not null
            ? WizardPlacement.Minimum(_scale, available)
            : WindowSizeMemory.Minimum(new Size(WindowSizeMemory.MinWidth, WindowSizeMemory.MinHeight), _scale, available);
        MinWidth  = minimum.Width;
        MinHeight = minimum.Height;
    }

    /// Call before the first Show(): sizes setup to the work area of the screen it opens on and centres it there.
    public void PlaceOnboarding() {
        var scale = _contentScale?.Current ?? _scale;
        if ((Screens?.ScreenFromWindow(this) ?? Screens?.Primary) is not { } screen) {
            Width = WizardPlacement.PreferredWidth;
            Height = WizardPlacement.PreferredHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var (position, size) = WizardPlacement.Place(scale, new ScreenArea(screen.Bounds, screen.WorkingArea, screen.Scaling));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = position;
        Width = size.Width;
        Height = size.Height;
    }

    // The inset is in window units and the pane sits inside the scale, so it is divided back out.
    void ApplyCaptionInset() => ContentPane.Margin = new Thickness(0, _captionTop / _scale, 0, 0);

    public MainWindow() {
        InitializeComponent();
        WindowControls.Spacing = RailClearance.Spacing;
        RailToggleButton.Margin = new Thickness(0, 0, RailClearance.ToggleGap - RailClearance.Spacing, 0);
        foreach (var button in new[] { RailToggleButton, BackButton, ForwardButton }) {
            button.MinWidth = button.MinHeight = RailClearance.ButtonSize;
            button.Padding = new Thickness((RailClearance.ButtonSize - 16) / 2);
        }
        // The negative right margin keeps a wide count from widening the toggle's slot and moving Back.
        var iconInset = (RailClearance.ButtonSize - 16) / 2;
        RailAttentionMark.Margin = new Thickness(iconInset + 8, iconInset - 4, -24, 0);
        SessionsSurface.SizeChanged += (_, _) => LimitRail();
        Panes.Changed += (_, _) => {
            ApplyRail();
            LimitRail();
        };
        ApplyRail();
        KeyBindings.AddRange(ZoomShortcut.For(this));
        // A move, a display scaling change and a resized work area each change what the screen offers.
        PositionChanged += (_, _) => RefreshMinimumSize();
        ScalingChanged += (_, _) => RefreshMinimumSize();
        Screens? watchedScreens = null;
        void OnScreensChanged(object? sender, EventArgs e) => RefreshMinimumSize();
        Opened += (_, _) => {
            if (watchedScreens is not null || Screens is not { } screens) return;
            watchedScreens = screens;
            screens.Changed += OnScreensChanged;
        };
        // Hide-to-tray never closes the window, so only a real close lets the setting and the screens go.
        Closed += (_, _) => {
            _contentScaleSubscription?.Dispose();
            if (watchedScreens is not null) watchedScreens.Changed -= OnScreensChanged;
        };
        BindRefreshShortcuts();
        BindSearchShortcuts();
        BindRailStepShortcuts();
        BindSidebarShortcut();
        BindFindShortcut();
        BindHistoryShortcuts();
        BindSettingsShortcut();
        KeepContentClearOfCaptionButtons();
        ShowInstanceTitle();
        BindFullScreenShortcut();
        Closing += (_, e) => {
            if (CloseInterceptor?.Invoke() == true) e.Cancel = true;
        };

        // Built on Loaded (window open), not here in the constructor: WindowNotificationManager
        // self-installs into the TopLevel's AdornerLayer once its template is applied, and Loaded
        // is when that is guaranteed. A hide-to-tray/reopen cycle (MainWindowCoordinator.Hide())
        // only toggles OS-level visibility — it never detaches this window from the visual tree —
        // so Loaded fires once per window instance and this null-guard is defensive only.
        Loaded += (_, _) => _notifications ??= new WindowNotificationManager(this) {
            Position = NotificationPosition.TopRight,
        };

        var switchCover = new WorkspaceSwitchCover(ContentPane, SwitchCover, SwitchCoverStill);
        this.WhenActivated(disposables => {
            Observable.Switch(this.WhenAnyValue(x => x.ViewModel)
                    .Select(vm => vm is null ? Observable.Return(RailAttentionSummary.None) : vm.WhenAnyValue(x => x.RailAttention)))
                .Subscribe(_ => ApplyRailAttention())
                .DisposeWith(disposables);
            var coverTracking = new SerialDisposable().DisposeWith(disposables);
            this.WhenAnyValue(x => x.ViewModel)
                .Subscribe(vm => coverTracking.Disposable = vm is null ? null : switchCover.Track(vm))
                .DisposeWith(disposables);
            // Switched over the ViewModel, not read once: an onboarding window activates before
            // the main surface hands it one.
            Observable.Switch(this.WhenAnyValue(x => x.ViewModel)
                    .Select(vm => vm is null
                        ? Observable.Return<ISessionWorkspace?>(null)
                        : vm.WhenAnyValue(x => x.CurrentWorkspace)))
                .Subscribe(workspace => {
                    if (workspace is null) Dispatcher.UIThread.Post(LauncherView.FocusComposer, DispatcherPriority.Loaded);
                    UpdateWorkspaceVisibility();
                })
                .DisposeWith(disposables);
            Observable.Switch(this.WhenAnyValue(x => x.ViewModel)
                    .Select(vm => vm?.LauncherNudges ?? Observable.Empty<System.Reactive.Unit>()))
                .Subscribe(_ => LauncherView.Nudge())
                .DisposeWith(disposables);
        });
    }

    // Meta is Command. Ctrl+R is added only where there is no Command key, and it yields while
    // the terminal has focus. Ctrl+Shift+R does not yield. macOS leaves Ctrl+R unbound.
    void BindRefreshShortcuts() {
        var bindings = new List<KeyBinding>();
        Add(new KeyGesture(Key.R, KeyModifiers.Meta));
        if (RefreshShortcut.FromTerminal is { } fromTerminal) {
            KeyBindings.Add(new KeyBinding {
                Gesture = RefreshShortcut.Primary,
                Command = new UnlessTerminalFocused(this, vm => vm.RefreshWorkCommand),
            });
            Add(fromTerminal);
        }

        void Apply() {
            if ((DataContext as MainWindowViewModel)?.RefreshWorkCommand is not { } command) return;
            foreach (var binding in bindings) binding.Command = command;
        }
        Apply();
        DataContextChanged += (_, _) => Apply();

        void Add(KeyGesture gesture) {
            var binding = new KeyBinding { Gesture = gesture };
            bindings.Add(binding);
            KeyBindings.Add(binding);
        }
    }

    // ⌘B on macOS; Ctrl+B elsewhere yields to a focused terminal, where it is a shell and tmux key.
    void BindSidebarShortcut() {
        var toggle = ReactiveCommand.Create(ToggleRail);
        KeyBindings.Add(ShortcutLabels.UsesCommandKey
            ? new KeyBinding { Gesture = new KeyGesture(Key.B, KeyModifiers.Meta), Command = toggle }
            : new KeyBinding { Gesture = new KeyGesture(Key.B, KeyModifiers.Control), Command = new UnlessTerminalFocused(this, toggle) });
    }

    // ⌘F and ⌘G on macOS. Off macOS Ctrl+F and F3 yield to a focused terminal, where they are shell
    // and TUI keys.
    void BindFindShortcut() {
        var open = ReactiveCommand.Create(() => FindTarget()?.OpenFind());
        var next = ReactiveCommand.Create(() => FindTarget()?.StepFind(backwards: false));
        var previous = ReactiveCommand.Create(() => FindTarget()?.StepFind(backwards: true));
        if (ShortcutLabels.UsesCommandKey) {
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, KeyModifiers.Meta), Command = open });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.G, KeyModifiers.Meta), Command = next });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.G, KeyModifiers.Meta | KeyModifiers.Shift), Command = previous });
        } else {
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, KeyModifiers.Control), Command = new UnlessTerminalFocused(this, open) });
        }
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F3), Command = new UnlessTerminalFocused(this, next) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F3, KeyModifiers.Shift), Command = new UnlessTerminalFocused(this, previous) });
    }

    /// The chat holding focus, else the first one showing: a subagent pane opens beside its parent's.
    ChatTabView? FindTarget() {
        var focused = FocusManager?.GetFocusedElement() as Visual;
        return focused?.GetSelfAndVisualAncestors().OfType<ChatTabView>().FirstOrDefault(view => view.IsEffectivelyVisible)
            ?? this.GetVisualDescendants().OfType<ChatTabView>().FirstOrDefault(view => view.IsEffectivelyVisible);
    }

    // ⌘K everywhere on macOS. Off macOS Ctrl+K is a terminal key, so it yields to a focused terminal.
    void BindSearchShortcuts() {
        var meta = new KeyBinding { Gesture = new KeyGesture(Key.K, KeyModifiers.Meta) };
        KeyBindings.Add(meta);
        if (!ShortcutLabels.UsesCommandKey) {
            KeyBindings.Add(new KeyBinding {
                Gesture = new KeyGesture(Key.K, KeyModifiers.Control),
                Command = new UnlessTerminalFocused(this, vm => vm.OpenSearchCommand),
            });
        }

        void Apply() {
            if ((DataContext as MainWindowViewModel)?.OpenSearchCommand is { } command) meta.Command = command;
        }
        Apply();
        DataContextChanged += (_, _) => Apply();
    }

    // Ctrl+PageUp and Ctrl+PageDown on every platform, terminal focus included: they step through
    // the sessions in the rail, which is how a user moves between agents' terminals.
    void BindRailStepShortcuts() {
        var previous = new KeyBinding { Gesture = new KeyGesture(Key.PageUp, KeyModifiers.Control) };
        var next = new KeyBinding { Gesture = new KeyGesture(Key.PageDown, KeyModifiers.Control) };
        KeyBindings.Add(previous);
        KeyBindings.Add(next);

        void Apply() {
            if ((DataContext as MainWindowViewModel)?.Rail is not { } rail) return;
            previous.Command = rail.OpenPreviousSessionCommand;
            next.Command = rail.OpenNextSessionCommand;
        }
        Apply();
        DataContextChanged += (_, _) => Apply();
    }

    // ⌘[ and ⌘] on macOS; Alt+Left and Alt+Right elsewhere, which yield to a focused terminal
    // because a shell reads them as word moves. The mouse's back and forward buttons work anywhere.
    void BindHistoryShortcuts() {
        if (ShortcutLabels.UsesCommandKey) {
            var back = new KeyBinding { Gesture = new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Meta) };
            var forward = new KeyBinding { Gesture = new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Meta) };
            KeyBindings.Add(back);
            KeyBindings.Add(forward);
            void Apply() {
                if (DataContext is not MainWindowViewModel vm) return;
                back.Command = vm.GoBackCommand;
                forward.Command = vm.GoForwardCommand;
            }
            Apply();
            DataContextChanged += (_, _) => Apply();
        } else {
            KeyBindings.Add(new KeyBinding {
                Gesture = new KeyGesture(Key.Left, KeyModifiers.Alt),
                Command = new UnlessTerminalFocused(this, vm => vm.GoBackCommand),
            });
            KeyBindings.Add(new KeyBinding {
                Gesture = new KeyGesture(Key.Right, KeyModifiers.Alt),
                Command = new UnlessTerminalFocused(this, vm => vm.GoForwardCommand),
            });
        }

        AddHandler(PointerPressedEvent, (_, e) => {
            if (DataContext is not MainWindowViewModel vm) return;
            var command = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch {
                PointerUpdateKind.XButton1Pressed => vm.GoBackCommand,
                PointerUpdateKind.XButton2Pressed => vm.GoForwardCommand,
                _ => null,
            };
            if (command is not System.Windows.Input.ICommand ui || !ui.CanExecute(null)) return;
            e.Handled = true;
            ui.Execute(null);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public const string TitleEnvVar = "KCAP_APP_TITLE";

    // Names the instance, so several running side by side can be told apart. Title carries it everywhere
    // (taskbar, Alt+Tab, screen readers); where the window paints into its title bar, whose drawn title
    // text is hidden, the app also draws it centred. Onboarding puts its own step title in the same spot,
    // so the label gives way to it.
    void ShowInstanceTitle() {
        if (Environment.GetEnvironmentVariable(TitleEnvVar) is not { } raw || raw.Trim() is not { Length: > 0 } title) return;
        InstanceTitle.Text = title;
        Title = title;
        InstanceTitle.Bind(IsVisibleProperty, this.GetObservable(IsExtendedIntoWindowDecorationsProperty)
            .CombineLatest(OnboardingPane.GetObservable(IsVisibleProperty), (extended, onboarding) => extended && !onboarding));
    }

    // Outside macOS the caption buttons sit over the top-right of the extended client area, so the
    // content pane starts below the title bar there.
    void KeepContentClearOfCaptionButtons() {
        if (OperatingSystem.IsMacOS()) return;
        this.GetObservable(WindowDecorationMarginProperty)
            .Subscribe(margin => {
                _captionTop = margin.Top;
                ApplyCaptionInset();
            });
    }

    // F11 is the Windows convention for full screen; Win+Up and Win+Down already maximize and
    // minimize, which the macOS Window menu has to provide itself.
    void BindFullScreenShortcut() {
        if (!OperatingSystem.IsWindows()) return;
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F11), Command = ReactiveCommand.Create(ToggleFullScreen) });
    }

    WindowState _beforeFullScreen = WindowState.Normal;

    internal void ToggleFullScreen() {
        if (WindowState == WindowState.FullScreen) {
            WindowState = _beforeFullScreen;
            return;
        }
        _beforeFullScreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        WindowState = WindowState.FullScreen;
    }

    // macOS takes ⌘, from the native app menu, which Windows does not draw.
    void BindSettingsShortcut() {
        if (!OperatingSystem.IsWindows()) return;
        var binding = new KeyBinding { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Control) };
        KeyBindings.Add(binding);

        void Apply() {
            if ((DataContext as MainWindowViewModel)?.OpenSettingsCommand is { } command) binding.Command = command;
        }
        Apply();
        DataContextChanged += (_, _) => Apply();
    }

    // A toast fired before Loaded, or while the window is hidden (Hide() suspends rendering
    // entirely), is invisible to the user — stderr (AppNotifier's own mirroring) is the only
    // channel that survives either case.
    void ShowToast(string message) =>
        _notifications?.Show(new Notification("kcap", message, NotificationType.Warning, ToastDuration(message)));

    // About fifteen characters a second to read it, never under four seconds or past twenty.
    internal static TimeSpan ToastDuration(string message) => TimeSpan.FromSeconds(Math.Clamp(message.Length / 15.0, 4, 20));

    // The launcher header strip and the bottom-most drag strip — see WindowChrome.
    void OnChromePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) =>
        WindowChrome.BeginDrag(this, e);

    // IsVisible is what Show()/Hide() toggle, and hide-to-tray never fires Closed/Opened, so this
    // property is the one signal that tracks on-screen state across a hide/reopen cycle.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        // DataContextProperty too — defensive: production always assigns DataContext before the
        // first Show(), but this keeps the check correct even if a caller (a test) does it the
        // other way around.
        if (change.Property == IsVisibleProperty || change.Property == DataContextProperty
            || change.Property == WindowStateProperty) UpdateWorkspaceVisibility();
    }

    // Visibility, never keyboard focus, keeps a PR reader's access lease renewed beside another app.
    void UpdateWorkspaceVisibility() {
        if (DataContext is MainWindowViewModel vm) {
            var pullRequests = vm.CurrentWorkspace?.PullRequests;
            if (_foregroundPullRequests != pullRequests) _foregroundPullRequests?.SetForeground(false);
            _foregroundPullRequests = pullRequests;
            pullRequests?.SetForeground(IsVisible && WindowState != Avalonia.Controls.WindowState.Minimized);
        } else {
            _foregroundPullRequests?.SetForeground(false);
            _foregroundPullRequests = null;
        }
    }
}
