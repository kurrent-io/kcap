using System.ComponentModel;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class MainWindow {
    internal const string CommandPaletteResource = "KcapDesktopCommandPalette";
    static KeyModifiers CommandModifier => ShortcutLabels.UsesCommandKey ? KeyModifiers.Meta : KeyModifiers.Control;
    internal static KeyGesture CommandPaletteShortcut { get; } = new(Key.P, CommandModifier | KeyModifiers.Shift);

    CommandPalette? _commandPalette;
    MainWindowViewModel? _commandPaletteOwner;
    Control? _focusBeforeCommandPalette;
    bool _commandPaletteEnabled;

    internal bool CanOpenCommandPalette => _commandPaletteEnabled && Onboarding is null && DataContext is MainWindowViewModel;
    internal event EventHandler? CommandPaletteAvailabilityChanged;

    void BindCommandPalette() {
        var flag = this.GetResourceObservable(CommandPaletteResource).Select(value => value is true).DistinctUntilChanged()
            .Subscribe(enabled => {
                _commandPaletteEnabled = enabled;
                UpdateCommandPaletteAvailability();
            });
        DataContextChanged += (_, _) => {
            CloseCommandPalette();
            UpdateCommandPaletteAvailability();
        };
        Closed += (_, _) => {
            flag.Dispose();
            CloseCommandPalette();
        };

        AddHandler(KeyDownEvent, OnCommandPaletteKeyDown, RoutingStrategies.Tunnel);
        AddHandler(GettingFocusEvent, (_, e) => {
            if (_commandPalette is { } palette && e.NewFocusedElement is Visual next && !palette.IsVisualAncestorOf(next))
                e.TrySetNewFocusedElement(palette.QueryField);
        }, RoutingStrategies.Tunnel);
    }

    void UpdateCommandPaletteAvailability() {
        if (!CanOpenCommandPalette) CloseCommandPalette();
        CommandPaletteAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    void OnCommandPaletteKeyDown(object? sender, KeyEventArgs e) {
        if (CommandPaletteShortcut.Matches(e) && CanOpenCommandPalette && !TerminalFocus.Has(this)) {
            e.Handled = true;
            if (_commandPalette is null) OpenCommandPalette();
            else CloseCommandPalette();
        } else if (_commandPalette is { } palette) {
            if (e.Key == Key.K && e.KeyModifiers == CommandModifier && ViewModel is { } vm) {
                e.Handled = true;
                CloseCommandPalette();
                var search = (ICommand)vm.OpenSearchCommand;
                if (search.CanExecute(null)) search.Execute(null);
            } else if (e.Key == Key.F6) {
                e.Handled = true;
            } else {
                palette.HandleKey(e);
            }
        }
    }

    internal void OpenCommandPalette() {
        if (!CanOpenCommandPalette || !IsVisible || IsCommandPaletteOpen() || ViewModel is not { } vm) return;
        _focusBeforeCommandPalette = FocusManager?.GetFocusedElement() as Control;
        var palette = new CommandPalette(CommandPaletteEntries(vm)) { DataContext = null };
        _commandPalette = palette;
        _commandPaletteOwner = vm;
        vm.PropertyChanged += OnCommandPaletteContextChanged;
        palette.Closed += CloseCommandPalette;
        ScaledSurface.Children.Add(palette);
        Dispatcher.UIThread.Post(() => {
            if (ReferenceEquals(_commandPalette, palette)) palette.QueryField.Focus();
        }, DispatcherPriority.Loaded);
    }

    void OnCommandPaletteContextChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentWorkspace) or nameof(MainWindowViewModel.IsSearchOpen))
            CloseCommandPalette();
    }

    partial void CloseCommandPalette() {
        if (_commandPalette is not { } palette) return;
        _commandPalette = null;
        if (_commandPaletteOwner is { } owner) owner.PropertyChanged -= OnCommandPaletteContextChanged;
        _commandPaletteOwner = null;
        palette.Closed -= CloseCommandPalette;
        palette.Dispose();
        ScaledSurface.Children.Remove(palette);
        RestoreWorkspaceFocus(_focusBeforeCommandPalette);
        _focusBeforeCommandPalette = null;
    }

    partial void CheckCommandPaletteOpen(ref bool open) => open = _commandPalette is not null;

    IEnumerable<CommandPaletteEntry> CommandPaletteEntries(MainWindowViewModel vm) {
        yield return new("Navigation", "Search sessions", vm.OpenSearchCommand, ShortcutLabels.Search);
        yield return new("Navigation", "New session", vm.NewSessionCommand, ShortcutLabels.NewSession);
        yield return new("Navigation", "Back", vm.GoBackCommand, HistoryShortcuts.Back.ToString("p", null));
        yield return new("Navigation", "Forward", vm.GoForwardCommand, HistoryShortcuts.Forward.ToString("p", null));
        yield return new("Navigation", "Refresh", vm.RefreshWorkCommand, RefreshShortcut.Label);

        var workspace = WorkspaceHost.Presenter?.Child;
        if (workspace?.DataContext == vm.CurrentWorkspace && workspace is WorkspaceView or RemoteSessionView) {
            foreach (var (name, label) in SessionPaletteButtons) {
                if (workspace.FindControl<Button>(name) is { Command: { } command } button)
                    yield return new("Session", label, command, Source: button);
            }
        }

        yield return new("Settings", "Settings", vm.OpenSettingsCommand,
            ShortcutLabels.UsesCommandKey ? "⌘," : OperatingSystem.IsWindows() ? "Ctrl+," : null);
        yield return new("Settings", "Launch approvals", vm.OpenLaunchApprovalsCommand);
    }

    static readonly (string Name, string? Label)[] SessionPaletteButtons = [
        ("ChatTabButton", "Show chat"),
        ("TerminalTabButton", "Show terminal"),
        ("PullRequestTabButton", "Show pull request"),
        ("ArtefactsTabButton", "Show pages"),
        ("OpenInWebButton", "Open session in web"),
        ("StopButton", null),
    ];
}
