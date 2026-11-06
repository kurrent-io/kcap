using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services.Update;

/// Owns the update schedule and its UX: silent periodic checks, background download, one prompt
/// when a package is ready, and the hand-off to the updater at the very end of shutdown. Once a
/// package is ready nothing else is checked or downloaded this run — a later download, even a
/// failed one, deletes every other cached package.
public sealed class UpdateCoordinator {
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(4);
    internal const string CheckLabel = "Check for Updates…";
    // Velopack's updater on Windows ends every process running from the install folder, the app's daemon
    // among them, so kcap stops the daemon at a turn boundary first; elsewhere a background daemon
    // restarts at one by itself.
    internal static string ReadyDisclosure(bool windows) => windows
        ? "kcap restarts to install the update.\n\nThe daemon waits for active turns, then stops. It stops anyway when the configured wait ends, interrupting long turns. A daemon that does not answer stops with kcap, mid-turn.\n\nEligible agents resume if auto-resume is on. Other sessions may need attention."
        : "kcap restarts to install the update.\n\nThe daemon waits for active turns. It restarts when the configured wait ends. Long turns may be interrupted.\n\nEligible agents resume if auto-resume is on. Other sessions may need attention. Daemons started in a terminal need a manual restart.";

    internal static string WaitingDisclosure(TimeSpan wait) =>
        $"The daemon stops once its agents finish their turns, so none is cut off. After {Describe(wait)} it stops anyway and interrupts the turns still running.\n\n" +
        "Restart now interrupts them at once. Not now keeps the update for later. Eligible agents resume if auto-resume is on.";

    static string Describe(TimeSpan wait) {
        var seconds = (long)Math.Ceiling(wait.TotalSeconds);
        if (seconds < 60 || seconds % 60 != 0) return seconds == 1 ? "1 second" : $"{seconds} seconds";
        var minutes = seconds / 60;
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    readonly IAppUpdater _updater;
    readonly ILifecycleSurface _surface;
    readonly TimeProvider _time;
    readonly Action _quit;
    readonly CancellationToken _lifetime;
    readonly TimeSpan _initialDelay;
    readonly TimeSpan _interval;
    readonly BehaviorSubject<UpdateMenuItem> _menu;
    readonly BehaviorSubject<UpdateCheckProgress?> _checkProgress = new(null);
    readonly Action<UpdateCandidate>? _handingOver;
    readonly LifecycleLog? _log;
    readonly IUpdateDaemonStop? _daemonStop;
    readonly Lock _lock = new();

    Task? _inflight;
    bool _stoppingDaemon;
    int _daemonHandoff;
    Task? _offer;
    bool _manualPending;
    bool _progressVisible;
    UpdateCheckProgress? _currentProgress;
    UpdateCandidate? _ready;
    UpdateCandidate? _pendingApply;

    /// The single-flight lane (`_inflight`) guarantees only one check ever sets this; `Withdraw` only
    /// clears it, by compare-and-swap. Readers run
    /// on the UI thread via `RunMenuActionAsync`, and `CheckAsync` holds `_lock` across
    /// `RunCheckAsync`'s synchronous prefix (which can reach `_menu.OnNext` and `ConfirmAsync`) — so
    /// this must never block on `_lock`, or a UI-thread caller blocked on the same dialog deadlocks.
    UpdateCandidate? Ready {
        get => Volatile.Read(ref _ready);
        set => Volatile.Write(ref _ready, value);
    }

    /// <param name="handingOver">Runs immediately before the updater takes over, so the next process can tell
    /// it finishes this update.</param>
    /// <param name="daemonStop">Stops the daemon before the restart, where the updater would end it mid-turn; null
    /// leaves it running.</param>
    public UpdateCoordinator(
            IAppUpdater updater, ILifecycleSurface surface, TimeProvider time, Action quit, CancellationToken lifetime,
            TimeSpan? initialDelay = null, TimeSpan? interval = null, Action<UpdateCandidate>? handingOver = null,
            LifecycleLog? log = null, IUpdateDaemonStop? daemonStop = null) {
        _updater      = updater;
        _surface      = surface;
        _time         = time;
        _quit         = quit;
        _lifetime     = lifetime;
        _initialDelay = initialDelay ?? DefaultInitialDelay;
        _interval     = interval ?? DefaultInterval;
        _menu         = new BehaviorSubject<UpdateMenuItem>(new UpdateMenuItem(updater.IsAvailable, CheckLabel));
        _handingOver  = handingOver;
        _log          = log;
        _daemonStop   = daemonStop;
    }

    public IObservable<UpdateMenuItem> MenuItem => _menu.AsObservable();
    public IObservable<UpdateCheckProgress?> CheckProgress => _checkProgress.DistinctUntilChanged();

    /// The version a restart would install, once its package is downloaded.
    public string? ReadyVersion => Ready?.Version;

    /// What the accepted restart did with the daemon before it quit.
    public DaemonHandoff DaemonHandoff => (DaemonHandoff)Volatile.Read(ref _daemonHandoff);

    /// Whether the update leaves no daemon running, for the next process to start.
    public bool DaemonLeftStopped => DaemonHandoff is DaemonHandoff.Stopped or DaemonHandoff.NotRunning;

    /// Applies a package left from a previous run, after the install-location guard and before any
    /// graph exists. Skipped on an update relaunch: a failed apply relaunches the old version with
    /// the same package still cached, and applying it again automatically would loop before any UI
    /// appeared. True means the process is being replaced.
    public static bool TryApplyPendingAtStartup(IAppUpdater updater, bool updateRelaunch,
            Action<UpdateCandidate>? handingOver = null, LifecycleLog? log = null) {
        if (!updater.IsAvailable || updateRelaunch) return false;
        if (updater.PendingRestart is not { } pending || !IsEligible(updater, pending)) return false;

        // A failed apply must fall through to the normal UI, where the schedule re-offers the
        // update visibly, rather than bricking every launch behind a repeating startup failure.
        try {
            handingOver?.Invoke(pending);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: could not record the update handover: {ex.Message}");
        }
        try {
            log?.Write("update_apply_pending", ("from", updater.InstalledVersion), ("to", pending.Version));
            updater.ApplyNow(pending);
            return true;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: applying the pending update failed: {ex.Message}");
            log?.Write("update_apply_failed", ("stage", "pending_at_startup"), ("to", pending.Version), ("error", ex.Message));
            return false;
        }
    }

    internal static bool IsEligible(IAppUpdater updater, UpdateCandidate candidate) {
        var installed = updater.InstalledVersion;
        if (installed is null) return false;
        if (candidate.IsPrerelease && !updater.AllowsPrerelease) return false;

        return PrereleaseSemver.IsNewer(candidate.Version, installed);
    }

    public void Start() {
        if (!_updater.IsAvailable) return;
        _ = RunScheduleAsync();
    }

    /// The tray item's action: a check while idle, the restart once a package is ready.
    public Task RunMenuActionAsync() {
        if (Offerable is { } ready) {
            RequestRestart(ready);
            return Task.CompletedTask;
        }

        return CheckAsync(manual: true);
    }

    /// The app menu's item, whose label never changes: a manual check while idle, the ready prompt
    /// again once a package is downloaded.
    public async Task CheckForUpdatesAsync() {
        if (Offerable is not { } ready) {
            await CheckAsync(manual: true).ConfigureAwait(false);
            return;
        }
        try {
            await OfferOnceAsync(ready).ConfigureAwait(false);
        } catch (OperationCanceledException) { }
    }

    /// A silent check now, after a change to which builds this install is offered. A downloaded
    /// package the change made ineligible stops being offered.
    public Task RecheckAsync() {
        if (!_updater.IsAvailable) return Task.CompletedTask;
        if (Ready is { } ready && !IsEligible(_updater, ready)) Withdraw(ready);
        lock (_lock) {
            // A check already running read the feed under the old choice, so this one runs after it.
            if (_inflight is { IsCompleted: false } running) return CheckAfterAsync(running);
            return CheckAsync(manual: false);
        }
    }

    async Task CheckAfterAsync(Task running) {
        await running.ConfigureAwait(false);
        await CheckAsync(manual: false).ConfigureAwait(false);
    }

    UpdateCandidate? Offerable => Ready is { } ready && IsEligible(_updater, ready) ? ready : null;

    void Withdraw(UpdateCandidate ready) {
        if (Interlocked.CompareExchange(ref _ready, null, ready) != ready) return;
        _log?.Write("update_withdrawn", ("to", ready.Version));
        _menu.OnNext(new UpdateMenuItem(true, CheckLabel));
    }

    /// Called by the shutdown sequence immediately before the platform shutdown call, so the
    /// updater's bounded wait only ever covers process exit.
    public void ApplyPendingOnExit() {
        UpdateCandidate? pending;
        lock (_lock) {
            pending = _pendingApply;
            _pendingApply = null;
        }
        if (pending is null) return;
        // The beta choice can change between accepting the restart and the exit.
        if (!IsEligible(_updater, pending)) {
            _log?.Write("update_withdrawn", ("to", pending.Version), ("stage", "on_exit"));
            return;
        }
        try {
            _handingOver?.Invoke(pending);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not record the update handover: {ex.Message}");
        }
        _log?.Write("update_apply_on_exit", ("from", _updater.InstalledVersion), ("to", pending.Version));
        _updater.ApplyOnExit(pending);
    }

    async Task RunScheduleAsync() {
        try {
            await Task.Delay(_initialDelay, _time, _lifetime).ConfigureAwait(false);
            while (!_lifetime.IsCancellationRequested) {
                await CheckAsync(manual: false).ConfigureAwait(false);
                await Task.Delay(_interval, _time, _lifetime).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) { }
    }

    Task CheckAsync(bool manual) {
        lock (_lock) {
            if (_inflight is { IsCompleted: false } running) {
                if (manual) {
                    _manualPending = true;
                    _progressVisible = true;
                    _checkProgress.OnNext(_currentProgress);
                }
                return running;
            }

            _progressVisible = manual;
            _inflight = RunCheckAsync(manual);
            return _inflight;
        }
    }

    async Task RunCheckAsync(bool manual) {
        var stage = "check";
        try {
            if (Ready is { } ready) {
                if (IsEligible(_updater, ready)) {
                    if (manual) await ReportAsync($"kcap {ready.Version} is ready to install.").ConfigureAwait(false);
                    return;
                }
                // The beta choice changed as this package was published, past RecheckAsync's withdrawal.
                Withdraw(ready);
            }

            SetProgress(new UpdateCheckProgress("Checking for updates…"));
            var candidate = await _updater.CheckAsync(_lifetime).ConfigureAwait(false);
            if (candidate is null) {
                SetProgress(null);
                if (manual) _log?.Write("update_check", ("result", "up_to_date"), ("manual", "true"));
                if (ConsumeManual(manual)) await ReportAsync("kcap is up to date").ConfigureAwait(false);
                return;
            }

            _log?.Write("update_available", ("from", _updater.InstalledVersion), ("to", candidate.Version), ("manual", manual ? "true" : "false"));
            stage = "download";
            var downloading = new UpdateCheckProgress($"Downloading kcap {candidate.Version}…");
            SetProgress(downloading);
            await _updater.DownloadAsync(candidate, _lifetime, percent => {
                lock (_lock) {
                    if (_currentProgress is not null)
                        SetProgress(downloading with { Percent = Math.Clamp(percent, 0, 100) });
                }
            }).ConfigureAwait(false);
            SetProgress(null);
            _log?.Write("update_downloaded", ("to", candidate.Version));
            stage = "prompt";
            Interlocked.Exchange(ref _ready, candidate);
            // The beta choice can change while the package downloads.
            if (!IsEligible(_updater, candidate)) {
                Withdraw(candidate);
                ConsumeManual(manual);
                return;
            }
            _menu.OnNext(new UpdateMenuItem(true, $"Restart to update to {candidate.Version}"));

            await OfferOnceAsync(candidate).ConfigureAwait(false);
            ConsumeManual(manual);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            SetProgress(null);
            Console.Error.WriteLine($"kcap: update {stage} failed: {ex.Message}");
            _log?.Write("update_failed", ("stage", stage), ("manual", manual ? "true" : "false"), ("error", ex.Message));
            if (ConsumeManual(manual))
                await ReportAsync(stage == "download"
                    ? "The update could not be downloaded. Try again later."
                    : "Could not check for updates. Try again later.").ConfigureAwait(false);
        } finally {
            SetProgress(null);
            ConsumeManual(manual);
        }
    }

    void SetProgress(UpdateCheckProgress? progress) {
        lock (_lock) {
            _currentProgress = progress;
            _checkProgress.OnNext(_progressVisible ? progress : null);
        }
    }

    /// One ready prompt at a time: a caller arriving while it is open joins it rather than queueing
    /// another behind it.
    Task OfferOnceAsync(UpdateCandidate candidate) {
        lock (_lock) {
            if (_offer is { IsCompleted: false } open) return open;
            _offer = OfferAsync(candidate);
            return _offer;
        }
    }

    async Task OfferAsync(UpdateCandidate candidate) {
        var prompt = new LifecyclePrompt(
            LifecyclePrompt.KindUpdateReady, candidate.Version, _updater.InstalledVersion, false,
            ReadyDisclosure(OperatingSystem.IsWindows()));
        var accepted = await _surface.ConfirmAsync(prompt, _lifetime).ConfigureAwait(false);
        _log?.Write(accepted ? "update_accepted" : "update_deferred", ("to", candidate.Version));
        // The beta choice can change while the prompt is open.
        if (accepted && Offerable == candidate) RequestRestart(candidate);
    }

    bool ConsumeManual(bool manual) {
        lock (_lock) {
            var pending = _manualPending;
            _manualPending = false;
            return manual || pending;
        }
    }

    void RequestRestart(UpdateCandidate candidate) {
        if (_daemonStop is null) {
            Quit(candidate);
            return;
        }
        lock (_lock) {
            if (_stoppingDaemon) return;
            _stoppingDaemon = true;
        }
        // Off the caller's thread, which can be the UI's: the stop reads the pid file and waits on the daemon.
        _ = Task.Run(() => StopDaemonThenQuitAsync(candidate));
    }

    void Quit(UpdateCandidate candidate) {
        lock (_lock) _pendingApply = candidate;
        _quit();
    }

    enum WaitAnswer { None, RestartNow, NotNow }

    async Task StopDaemonThenQuitAsync(UpdateCandidate candidate) {
        var quit = false;
        try {
            _log?.Write("update_daemon_stopping", ("to", candidate.Version));
            var (handoff, answer) = await StopDaemonAsync(candidate).ConfigureAwait(false);
            if (answer == WaitAnswer.NotNow) {
                _log?.Write("update_deferred", ("to", candidate.Version), ("stage", "daemon_wait"));
                return;
            }
            Volatile.Write(ref _daemonHandoff, (int)handoff);
            _log?.Write("update_daemon_handoff", ("to", candidate.Version), ("result", handoff.ToString()),
                ("restart_now", answer == WaitAnswer.RestartNow ? "true" : null));
            quit = true;
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            // The update still applies: the updater ends the daemon as it always did.
            Console.Error.WriteLine($"kcap: stopping the daemon for the update failed: {ex.Message}");
            Volatile.Write(ref _daemonHandoff, (int)DaemonHandoff.Interrupted);
            _log?.Write("update_daemon_handoff", ("to", candidate.Version), ("result", nameof(DaemonHandoff.Interrupted)), ("error", ex.Message));
            quit = !_lifetime.IsCancellationRequested;
        } finally {
            if (!quit) {
                _daemonStop!.Release();
                lock (_lock) _stoppingDaemon = false;
            }
        }
        if (quit) Quit(candidate);
    }

    /// <summary>While the daemon holds the stop for its agents' turns, the user can cut the wait short or put the
    /// update off; the prompt closes by itself once the daemon settles first.</summary>
    async Task<(DaemonHandoff Handoff, WaitAnswer Answer)> StopDaemonAsync(UpdateCandidate candidate) {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        using var settled = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var answer = (int)WaitAnswer.None;
        var answered = 0;
        var asked = 0;
        Task? prompt = null;

        void Waiting(TimeSpan wait) {
            if (Interlocked.Exchange(ref asked, 1) != 0) return;
            Volatile.Write(ref prompt, AskWhileWaitingAsync());

            async Task AskWhileWaitingAsync() {
                await Task.Yield();
                var shown = new LifecyclePrompt(LifecyclePrompt.KindUpdateWaiting, candidate.Version, _updater.InstalledVersion, false,
                    WaitingDisclosure(wait));
                var accepted = await _surface.ConfirmAsync(shown, settled.Token).ConfigureAwait(false);
                if (settled.IsCancellationRequested || Interlocked.Exchange(ref answered, 1) != 0) return;
                Volatile.Write(ref answer, (int)(accepted ? WaitAnswer.RestartNow : WaitAnswer.NotNow));
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }

        DaemonHandoff handoff;
        try {
            handoff = await _daemonStop!.StopAsync(Waiting, stop.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) {
            handoff = DaemonHandoff.Interrupted;
        }
        Interlocked.Exchange(ref answered, 1);
        await settled.CancelAsync().ConfigureAwait(false);
        if (Volatile.Read(ref prompt) is { } open) {
            try {
                await open.ConfigureAwait(false);
            } catch (OperationCanceledException) { }
        }

        return ((WaitAnswer)Volatile.Read(ref answer)) switch {
            WaitAnswer.RestartNow => (DaemonHandoff.Interrupted, WaitAnswer.RestartNow),
            WaitAnswer.NotNow => (handoff, WaitAnswer.NotNow),
            _ => (handoff, WaitAnswer.None),
        };
    }

    Task ReportAsync(string message) =>
        _surface.ConfirmAsync(new LifecyclePrompt(LifecyclePrompt.KindUpdateInfo, null, _updater.InstalledVersion, false, message), _lifetime);
}
