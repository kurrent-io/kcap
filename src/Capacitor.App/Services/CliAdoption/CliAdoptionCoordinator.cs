namespace Capacitor.App.Services.CliAdoption;

/// <summary>Runs the takeover of an npm or script kcap install: once per launch, off the UI thread, and then only the
/// deletion check every <see cref="RecheckInterval"/> while the record is pending. The shim offer waits on
/// <see cref="FirstLook"/>, which completes however the first run ends, so it never confirms a link the switch is
/// about to make.</summary>
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
    Task _settled = Task.CompletedTask;
    int _started;
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

    public void Dispose() {
        lock (_lock) {
            _disposed = true;
            _recheck?.Dispose();
            _recheck = null;
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

    // A recheck never queues behind a run still going: the next tick comes soon enough.
    void Tick() {
        if (_lifetime.IsCancellationRequested || _batch is not { } batch || !_running.Wait(0)) return;
        var recheck = RunAsync(batch.RecheckAsync);
        lock (_lock) _settled = recheck;
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
