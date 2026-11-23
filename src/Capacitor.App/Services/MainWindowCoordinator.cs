using Capacitor.App.Views;

namespace Capacitor.App.Services;

/// Retains the main window across hide-to-tray cycles. Avalonia cannot show a window after a
/// real close, so only that path discards the instance.
/// <param name="releaseWorkspace">
/// Releases the workspace on a real close. The callback must tolerate a workspace that has already
/// been released.
/// </param>
/// <param name="parkWorkspace">
/// Releases the workspace on hide-to-tray, remembering what was open: an invisible terminal must
/// not keep clamping the PTY for other viewers. Falls back to releaseWorkspace.
/// </param>
/// <param name="restoreWorkspace">Reopens what the last hide parked, before the window reappears.</param>
/// <param name="onHidden">Runs after a user's close hid the window instead of closing it.</param>
/// <param name="onShown">Runs after the window is shown.</param>
public sealed class MainWindowCoordinator(
        Func<MainWindow> windowFactory, Action<MainWindow>? releaseWorkspace = null,
        Action<MainWindow>? parkWorkspace = null, Action<MainWindow>? restoreWorkspace = null,
        Action? onHidden = null, Action? onShown = null) {
    WindowRestore _placement = new();

    /// Prevents reopening and lets window teardown complete during deferred shutdown.
    public bool QuitInProgress { get; set; }

    /// The retained window, whether visible or hidden; null after a real close.
    public MainWindow? Window { get; private set; }

    /// While set, every request to show the main window, the Dock, tray, menu and notifications included,
    /// raises this instead: a launch finishing an update keeps its progress in front until that settles.
    public Action? ShowInstead { get; set; }

    public void ShowMainWindow() => ShowMainWindow(navigate: null);

    /// `navigate` runs before the window reappears, so a hidden window opens straight onto where the
    /// caller is going instead of first rebuilding what it was showing when it hid.
    public void ShowMainWindow(Action<MainWindow>? navigate) {
        if (QuitInProgress) return;
        if (ShowInstead is { } instead) {
            instead();
            if (Window is not null) navigate?.Invoke(Window);
            return;
        }

        Prepare();

        var window = Window!;
        navigate?.Invoke(window);
        if (!window.IsVisible) restoreWorkspace?.Invoke(window);
        _placement.BeforeShow(window);
        window.Show();
        window.Activate();
        onShown?.Invoke();
    }

    /// Builds the window without showing it, for a launch that has something to finish first.
    public void Prepare() {
        if (QuitInProgress) return;

        if (Window is null) {
            var window = windowFactory();
            window.CloseInterceptor = OnWindowClosing;
            window.Closed += (_, _) => {
                // A late close must release its own workspace without discarding a newer window.
                releaseWorkspace?.Invoke(window);
                if (ReferenceEquals(Window, window)) Window = null;
            };
            Window = window;
            _placement = new WindowRestore();
            _placement.Track(window);
        }
    }

    /// Returns true when the close must be cancelled because the window was hidden instead.
    public bool OnWindowClosing() {
        if (QuitInProgress) return false;

        if (Window is not null) {
            (parkWorkspace ?? releaseWorkspace)?.Invoke(Window);
            _placement.Hide(Window);
            onHidden?.Invoke();
        }
        return true;
    }
}
