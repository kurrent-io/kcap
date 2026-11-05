using System.Reactive.Linq;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Runs the takeover of an npm or script kcap install: once per launch, off the UI thread, then every
/// <see cref="RecheckInterval"/> while the record is pending. A recheck is the deletion check alone, unless a daemon
/// move is waiting for a safe point: then the whole batch runs again from fresh evidence, on the tick and as soon as
/// the daemon reports it is idle. One run at a time. The shim offer waits on <see cref="FirstLook"/>, which completes
/// however the first run ends, so it never confirms a link the switch is about to make.</summary>
internal sealed class CliAdoptionCoordinator : IDisposable {
    internal const string CliOverride = "KCAP_APP_CLI_PATH";
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(15);

    readonly AdoptionBatch? _batch;
    readonly TimeProvider _time;
    readonly Action<string, (string Key, string? Value)[]> _log;
    readonly CancellationToken _lifetime;
    readonly TaskCompletionSource _firstLook = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly SemaphoreSlim _running = new(1, 1);
    readonly CancellationTokenRegistration _stopped;
    readonly Lock _lock = new();
    ITimer? _recheck;
    IDisposable? _following;
    Task _settled = Task.CompletedTask;
    int _started;
    int _again;
    bool _disposed;

    /// <param name="batch">Null where this app never takes over an install: off macOS, or not run from an installed
    /// bundle.</param>
    public CliAdoptionCoordinator(
            AdoptionBatch? batch, TimeProvider time, Action<string, (string Key, string? Value)[]> log, CancellationToken lifetime) {
        _batch    = batch;
        _time     = time;
        _log      = log;
        _lifetime = lifetime;
        _stopped  = lifetime.Register(() => _firstLook.TrySetResult());
    }

    public Task FirstLook => _firstLook.Task;

    /// <summary>The run started last, the first one or a recheck.</summary>
    internal Task Settled {
        get { lock (_lock) return _settled; }
    }

    /// <param name="after">Settles first: the setup wizard's PATH link, which can outlive the wizard.</param>
    public void Start(Task? after = null) {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        if (_batch is not { } batch || _lifetime.IsCancellationRequested
         || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CliOverride))) {
            _firstLook.TrySetResult();
            return;
        }

        var first = Task.Run(() => FirstAsync(batch, after ?? Task.CompletedTask));
        lock (_lock) _settled = first;
    }

    /// <summary>Runs a batch whose daemon move waits for a safe point again each time the daemon turns idle.</summary>
    public void FollowDaemon(IObservable<DaemonActivity?> activity) {
        var following = activity.Select(a => a is { Busy: false }).DistinctUntilChanged().Where(idle => idle).Subscribe(_ => Retry());
        lock (_lock) {
            if (!_disposed) {
                _following?.Dispose();
                _following = following;
                return;
            }
        }
        following.Dispose();
    }

    public void Dispose() {
        lock (_lock) {
            _disposed = true;
            _recheck?.Dispose();
            _recheck = null;
            _following?.Dispose();
            _following = null;
        }
        _stopped.Dispose();
        _firstLook.TrySetResult();
        _batch?.Dispose();
    }

    async Task FirstAsync(AdoptionBatch batch, Task after) {
        try {
            await Task.WhenAny(after).ConfigureAwait(false);
            await _running.WaitAsync(_lifetime).ConfigureAwait(false);
            await RunAsync(batch.RunOnceAsync).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } finally {
            _firstLook.TrySetResult();
        }
    }

    // A tick never queues behind a run still going: the next one comes soon enough.
    void Tick() {
        if (_lifetime.IsCancellationRequested || _batch is not { } batch || !_running.Wait(0)) return;
        Launch(batch.AwaitingDaemon ? batch.RunOnceAsync : batch.RecheckAsync);
    }

    // An idle report that arrives during a run is kept for when it ends: that run may be the one that deferred.
    void Retry() {
        if (_lifetime.IsCancellationRequested || _batch is not { } batch) return;
        Interlocked.Exchange(ref _again, 1);
        if (!_running.Wait(0)) return;
        Interlocked.Exchange(ref _again, 0);
        if (batch.AwaitingDaemon) Launch(batch.RunOnceAsync);
        else _running.Release();
    }

    // Entered holding _running.
    void Launch(Func<CancellationToken, Task<bool>> run) {
        var task = Task.Run(() => RunAsync(run));
        lock (_lock) _settled = task;
    }

    // Entered holding _running.
    async Task RunAsync(Func<CancellationToken, Task<bool>> run) {
        try {
            Schedule(await run(_lifetime).ConfigureAwait(false));
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: switching to the app's kcap failed unexpectedly: {ex}");
            _log("adoption_held", [("reason", "unexpected_error"), ("result", $"{ex.GetType().Name}: {ex.Message}")]);
        } finally {
            _running.Release();
            if (Interlocked.Exchange(ref _again, 0) == 1) Retry();
        }
    }

    void Schedule(bool pending) {
        lock (_lock) {
            if (_disposed) return;
            if (pending) _recheck ??= _time.CreateTimer(_ => Tick(), null, RecheckInterval, RecheckInterval);
            else {
                _recheck?.Dispose();
                _recheck = null;
            }
        }
    }
}
