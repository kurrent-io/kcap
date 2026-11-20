using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.Services.Update;

namespace Capacitor.App.Views;

/// Avalonia exports this menu once; update its items without replacing the exported menu. Off macOS
/// nothing draws it natively, so <see cref="AppMenuFlyout"/> draws it in the main window, and there an
/// item with nothing to run is hidden rather than left disabled.
public sealed class AppMenu {
    readonly bool _inWindow;
    readonly NativeMenuItem _settings;
    readonly BehaviorSubject<Action?> _showSettings = new(null);
    // A fixed label: renaming an exported item makes Avalonia rebuild the native menu.
    readonly NativeMenuItem _checkForUpdates = new(UpdateCoordinator.CheckLabel) { IsEnabled = false };
    Func<Task>? _runCheckForUpdates;
    readonly NativeMenuItem _setup = new("Set up kcap…") { IsEnabled = false };
    Action? _showSetup;
    readonly NativeMenuItem _uninstall = new("Uninstall kcap…") { IsEnabled = false };
    Action? _runUninstall;

    /// <param name="showAbout">Null where the platform has no About to show, which hides the item.</param>
    /// <param name="inWindow">Whether the menu is drawn in the window rather than by the OS: true off macOS.</param>
    public AppMenu(Action? showAbout, bool? inWindow = null) {
        _inWindow = inWindow ?? !OperatingSystem.IsMacOS();
        _settings = new NativeMenuItem("Settings…") { Gesture = SettingsGesture(_inWindow), IsEnabled = false };
        var about = new NativeMenuItem("About kcap") { IsVisible = showAbout is not null };
        about.Click += (_, _) => showAbout?.Invoke();
        _checkForUpdates.Click += (_, _) => _ = _runCheckForUpdates?.Invoke();
        _settings.Click += (_, _) => _showSettings.Value?.Invoke();
        _setup.Click += (_, _) => _showSetup?.Invoke();
        _uninstall.Click += (_, _) => _runUninstall?.Invoke();
        if (_inWindow) {
            _checkForUpdates.IsVisible = false;
            _uninstall.IsVisible = false;
        }
        Menu = new NativeMenu {
            about, _checkForUpdates, new NativeMenuItemSeparator(), _settings, _setup, new NativeMenuItemSeparator(), _uninstall,
        };
    }

    // Only Windows binds Ctrl+, in the main window; a gesture shown in the menu must work.
    static KeyGesture? SettingsGesture(bool inWindow) =>
        !inWindow ? new KeyGesture(Key.OemComma, KeyModifiers.Meta)
        : OperatingSystem.IsWindows() ? new KeyGesture(Key.OemComma, KeyModifiers.Control)
        : null;

    public NativeMenu Menu { get; }

    /// The current Settings action, for the main window's Ctrl+, off macOS.
    public IObservable<Action?> SettingsAction => _showSettings;

    public void SetSettingsAction(Action? showSettings) {
        _settings.IsEnabled = showSettings is not null;
        _showSettings.OnNext(showSettings);
    }

    public void SetCheckForUpdatesAction(Func<Task>? check) {
        _checkForUpdates.IsEnabled = check is not null;
        if (_inWindow) _checkForUpdates.IsVisible = check is not null;
        _runCheckForUpdates = check;
    }

    public void SetSetupAction(Action? showSetup) {
        _showSetup = showSetup;
        _setup.IsEnabled = showSetup is not null;
    }

    public void SetUninstallAction(Action? uninstall) {
        _uninstall.IsEnabled = uninstall is not null;
        if (_inWindow) _uninstall.IsVisible = uninstall is not null;
        _runUninstall = uninstall;
    }
}
