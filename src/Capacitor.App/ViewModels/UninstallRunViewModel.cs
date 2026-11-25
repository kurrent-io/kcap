using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Runs the CLI uninstall, then removes the app itself. Anything that fails before the bundle is
/// trashed leaves the bundle in place, so the app never trashes itself half-cleaned.
public sealed class UninstallRunViewModel : ReactiveObject {
    readonly IKcapCli      _cli;
    readonly IAppRemoval   _removal;
    readonly bool          _purgeLocal;
    readonly bool          _keepConfig;
    readonly Func<Task>?    _prepare;
    readonly Func<bool>     _hasSettings;
    bool                   _prepared;
    bool?                  _hadSettings;
    bool?                  _hadCommand;
    UninstallRunState      _state = UninstallRunState.Running;
    string?                _failureMessage;

    public UninstallRunViewModel(IKcapCli cli, IAppRemoval removal, bool purgeLocal, Action quit,
            Func<Task>? prepare = null, bool keepConfig = false, Func<bool>? hasSettings = null) {
        _cli        = cli;
        _removal    = removal;
        _purgeLocal = purgeLocal;
        _keepConfig = keepConfig;
        _prepare    = prepare;
        _hasSettings = hasSettings ?? (static () => true);
        _prepared   = prepare is null;
        _state      = _prepared ? UninstallRunState.Running : UninstallRunState.Preparing;

        var state = this.WhenAnyValue(vm => vm.State);
        RetryCommand = ReactiveCommand.CreateFromTask(RunAsync, this.WhenAnyValue(vm => vm.CanRetry));
        CloseCommand = ReactiveCommand.Create(quit, state.Select(s => s is UninstallRunState.Failed or UninstallRunState.TrashFailed or UninstallRunState.Finished));
        ToggleDetailsCommand = ReactiveCommand.Create(() => DetailsExpanded = !DetailsExpanded);
    }

    public UninstallRunState State {
        get => _state;
        private set {
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(Heading));
            this.RaisePropertyChanged(nameof(CanRetry));
            this.RaisePropertyChanged(nameof(IsFinished));
            this.RaisePropertyChanged(nameof(IsBusy));
            this.RaisePropertyChanged(nameof(Description));
            this.RaisePropertyChanged(nameof(ProgressValue));
            this.RaisePropertyChanged(nameof(CloseButtonText));
            this.RaisePropertyChanged(nameof(DetailsLabel));
            if (value is UninstallRunState.Failed or UninstallRunState.TrashFailed) DetailsExpanded = true;
        }
    }

    public bool CanRetry => _prepared && State == UninstallRunState.Failed;
    public bool IsFinished => State == UninstallRunState.Finished;
    public bool IsBusy => State is UninstallRunState.Preparing or UninstallRunState.Running;
    public double ProgressValue => IsFinished ? 100 : 0;
    public string CloseButtonText => IsFinished ? "Done" : "Close";

    public bool DetailsExpanded {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(DetailsLabel));
        }
    }

    public string DetailsLabel => (DetailsExpanded, IsFinished) switch {
        (false, true)  => "Show what was removed",
        (true, true)   => "Hide what was removed",
        (false, false) => "Show details",
        (true, false)  => "Hide details",
    };

    public string Description => State switch {
        UninstallRunState.Preparing => "Closing kcap before removing its background services and app.",
        UninstallRunState.Finished => $"kcap is out of your coding agents, and none of its background services is left. {_removal.RemovedSummary}",
        UninstallRunState.Failed => _failureMessage ?? "Uninstall could not finish.",
        UninstallRunState.TrashFailed => $"{_removal.NotRemoved}. See the details below to finish.",
        _ => "Removing background services, coding-agent integrations, and the app.",
    };

    public string Heading => State switch {
        UninstallRunState.Failed      => "kcap was not fully uninstalled",
        UninstallRunState.TrashFailed => $"{_removal.FinishByHand} to finish",
        UninstallRunState.Finished    => "kcap is uninstalled",
        _                             => "Uninstalling kcap…",
    };

    public ObservableCollection<string> Lines { get; } = [];

    public ObservableCollection<UninstallReceiptItem> Receipt { get; } = [];

    public ReactiveCommand<Unit, Unit> RetryCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleDetailsCommand { get; }

    /// Call on the UI thread; only streamed lines arrive through the injected post.
    public async Task RunAsync() {
        _failureMessage = null;
        Lines.Clear();
        Receipt.Clear();

        // Running disables Close, so no exception may leave the run there.
        try {
            if (!_prepared) {
                State = UninstallRunState.Preparing;
                Lines.Add("Closing kcap…");
                await _prepare!();
                _prepared = true;
            }
            State = UninstallRunState.Running;
            await RunStepsAsync();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: uninstall stopped: {ex.Message}");
            Fail("Uninstall could not finish. Close and try again.");
        }
    }

    async Task RunStepsAsync() {
        StreamingResult result;
        var purged = 0;
        Lines.Add("Removing kcap’s background services and coding-agent integrations…");
        // What was there, read before anything goes and kept across a retry, whose first attempt may have removed it.
        // The readings touch the disk, so they stay off the thread that draws the progress window.
        if (_hadSettings is null || _hadCommand is null) {
            var (settings, command) = await Task.Run(() => (Observed(_hasSettings), Observed(() => _removal.CommandInstalled)));
            _hadSettings ??= settings;
            _hadCommand  ??= command;
        }
        try {
            result = await _cli.UninstallAsync(_purgeLocal, _keepConfig, line => {
                // The CLI's own outcome line: a purge that was asked for may have found no server to purge.
                if (line.Text.StartsWith("Purged the local server", StringComparison.Ordinal)) Interlocked.Exchange(ref purged, 1);
                Console.Error.WriteLine($"kcap uninstall: {line.Text}");
            }, CancellationToken.None);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not run uninstall: {ex.Message}");
            Fail("Could not start uninstall. Try again.");
            return;
        }

        if (result is not { ExitCode: 0, TimedOut: false }) {
            foreach (var line in result.Tail) Console.Error.WriteLine($"kcap uninstall: {line.Text}");
            Fail(result switch {
                { TimedOut: true } => "Uninstall took too long. The app is still installed. Try again.",
                { ExitCode: -1 } when result.Tail.Any(line => line.Text == "kcap CLI not found") =>
                    "kcap’s command-line tool is missing. Reinstall kcap, then try uninstalling again.",
                _ => "Could not finish removing kcap’s background services or integrations. The app is still installed. Try again.",
            });
            return;
        }

        IReadOnlyList<string> left;
        try {
            left = await _removal.RemoveLinksAsync(CancellationToken.None);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not remove the command links: {ex.Message}");
            Fail("Could not remove the kcap command from your terminal. Try again.");
            return;
        }

        if (left.Count > 0) {
            foreach (var path in left) Lines.Add($"Could not remove {path}");
            Fail("The app was left in place. Remove these links, or retry.");
            return;
        }
        Lines.Add("Removed kcap command links.");

        var supportFilesLeft = false;
        try {
            Lines.Add("Cleaning up app support files…");
            supportFilesLeft = !_removal.DeleteSupportFiles();
        } catch (Exception ex) {
            supportFilesLeft = true;
            Lines.Add("Some app support files could not be removed.");
            Console.Error.WriteLine($"kcap: could not remove the app's support files: {ex.Message}");
        }

        bool trashed;
        try {
            trashed = await _removal.TrashBundleAsync(CancellationToken.None);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not remove the app: {ex.Message}");
            trashed = false;
        }

        if (!trashed) {
            _removal.RevealBundle();
            Lines.Add($"Everything else is removed. {_removal.FinishByHand} to finish.");
            State = UninstallRunState.TrashFailed;
            return;
        }

        Lines.Add(_removal.RemovedLine);
        FillReceipt(Volatile.Read(ref purged) == 1, supportFilesLeft);
        State = UninstallRunState.Finished;
    }

    /// Claims only what a zero exit from `kcap uninstall` and the steps after it guarantee. That exit says nothing is
    /// left, not that something was there, so a line says "removed" or "kept" only for what was seen beforehand.
    void FillReceipt(bool purgedLocal, bool supportFilesLeft) {
        Receipt.Add(new("kcap is out of your coding agents’ user settings"));
        Receipt.Add(new("No kcap background service is left"));
        if (purgedLocal) Receipt.Add(new("Deleted the local server and every session it recorded"));
        if (_hadSettings == true)
            Receipt.Add(new(_keepConfig ? "Kept your kcap settings and sign-in for a reinstall" : "Removed your kcap settings and sign-in"));
        if (_hadCommand == true) Receipt.Add(new("Removed the kcap command the app installed"));
        Receipt.Add(new(_removal.Trashed, Done: !_removal.RemovedOnExit));
        if (supportFilesLeft)
            Receipt.Add(new("A few of the app’s own files couldn’t be removed. They don’t keep kcap running.", Done: false));
    }

    /// A reading that fails counts as seen: the removal then ran against it, and its own failure would have stopped the run.
    static bool Observed(Func<bool> read) {
        try {
            return read();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not read what the uninstall removes: {ex.Message}");
            return true;
        }
    }

    void Fail(string message) {
        _failureMessage = message;
        State = UninstallRunState.Failed;
    }
}
