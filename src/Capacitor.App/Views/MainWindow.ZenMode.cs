using System.Reactive.Linq;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

public partial class MainWindow {
    internal const string ZenModeResource = "KcapDesktopZenMode";

    Button? _zenExit;
    Control? _focusBeforeZen;
    ISessionWorkspace? _workspaceBeforeZen;

    internal bool IsInZenMode => _zenExit is not null;
    internal bool ZenModeEnabled { get; private set; }
    internal bool CanToggleZenMode => ZenModeEnabled && Onboarding is null &&
        (IsInZenMode || ViewModel is { CurrentWorkspace: not null, IsSearchOpen: false });
    internal event EventHandler? ZenModeAvailabilityChanged;
    internal event EventHandler? ZenModeChanged;
    double ZenExitExtraWidth => _zenExit is { } exit ? Math.Max(0, exit.Bounds.Width - RailClearance.ButtonSize) : 0;
    internal double WorkspaceWidthWithRail => SessionsSurface.Bounds.Width -
        (Panes.RailCollapsed ? 0 : Math.Min(Panes.RailWidth, RailColumn.MaxWidth));

    void BindZenMode() {
        var flag = this.GetResourceObservable(ZenModeResource).Select(value => value is true).DistinctUntilChanged()
            .Subscribe(enabled => {
                ZenModeEnabled = enabled;
                UpdateZenModeAvailability();
            });
        var context = Observable.Switch(this.WhenAnyValue(window => window.ViewModel)
            .Select(vm => vm is null
                ? Observable.Return(false)
                : vm.WhenAnyValue(x => x.CurrentWorkspace, x => x.IsSearchOpen, (workspace, search) => workspace is not null && !search)))
            .Subscribe(_ => UpdateZenModeAvailability());
        Closed += (_, _) => {
            flag.Dispose();
            context.Dispose();
        };
    }

    void UpdateZenModeAvailability() {
        if (IsInZenMode && (!ZenModeEnabled || Onboarding is not null || ViewModel?.CurrentWorkspace is null)) ExitZenMode();
        ZenModeAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ToggleZenMode() {
        if (!CanToggleZenMode || !IsVisible) return;
        CloseCommandPalette();
        if (IsInZenMode) ExitZenMode();
        else EnterZenMode();
    }

    void EnterZenMode() {
        _focusBeforeZen = FocusManager?.GetFocusedElement() as Control;
        _workspaceBeforeZen = ViewModel?.CurrentWorkspace;
        var slot = (Panel)RailToggleButton.Parent!;
        var exit = _zenExit = CreateZenExit();
        slot.Children.Insert(0, exit);
        RailToggleButton.IsVisible = false;
        ApplyZenMode();
        if (_focusBeforeZen is not { IsEffectivelyVisible: true }) {
            Dispatcher.UIThread.Post(() => {
                if (ReferenceEquals(_zenExit, exit)) exit.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    Button CreateZenExit() {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new AppIcon { Kind = LucideIconKind.PanelLeftOpen, Size = 16 });
        content.Children.Add(new TextBlock { Text = "Exit Zen", VerticalAlignment = VerticalAlignment.Center });
        var exit = new Button {
            Content = content, MinHeight = RailClearance.ButtonSize, Padding = RailToggleButton.Padding,
            Margin = RailToggleButton.Margin,
        };
        exit.Classes.Add("kcapGhost");
        AutomationProperties.SetLandmarkType(exit, AutomationLandmarkType.Navigation);
        exit.Click += (_, _) => ExitZenMode();
        exit.SizeChanged += (_, _) => ApplyRailClearance();
        return exit;
    }

    void ExitZenMode() {
        if (_zenExit is not { } exit) return;
        var workspace = ViewModel?.CurrentWorkspace;
        var previous = ReferenceEquals(workspace, _workspaceBeforeZen) ? _focusBeforeZen : null;
        var current = FocusManager?.GetFocusedElement() as Control;
        _focusBeforeZen = null;
        _workspaceBeforeZen = null;
        _zenExit = null;
        ((Panel)RailToggleButton.Parent!).Children.Remove(exit);
        RailToggleButton.IsVisible = true;
        ApplyZenMode();

        // The inspector fits again after the rail has taken its share of the current width.
        Dispatcher.UIThread.Post(() => {
            if (IsInZenMode || !IsVisible || Onboarding is not null || IsCommandPaletteOpen()) return;
            if (!ReferenceEquals(workspace, ViewModel?.CurrentWorkspace)) return;
            RestoreWorkspaceFocus(previous is { IsEffectivelyVisible: true } ? previous : current);
        }, DispatcherPriority.Loaded);
    }

    void ApplyZenMode() {
        Classes.Set("zenMode", IsInZenMode);
        ApplyRail();
        ZenModeChanged?.Invoke(this, EventArgs.Empty);
        ZenModeAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
