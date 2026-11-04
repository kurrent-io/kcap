using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services.LocalSetup;

/// <summary>
/// One `kcap local fetch --json` per app lifetime, started while the edition page offers the local edition so the
/// install finds the bundle already in the CLI's cache. A cancelled or failed fetch is not retried: the install
/// downloads whatever the cache lacks.
/// </summary>
public sealed class LocalBundlePrefetch(Func<IKcapCli> cli, CancellationToken lifetime) {
    /// What `kcap local fetch` exits with when the bundle's manifest answered 404.
    public const int NotPublishedExit = 4;

    readonly Lock _lock = new();
    CancellationTokenSource? _interrupt;
    Task _completion = Task.CompletedTask;
    bool _started;
    volatile bool _notPublished;

    /// Each stdout line the fetch prints, raised on the thread that read it.
    public event Action<LocalSetupLine>? Line;

    /// Raised once, on the thread that read the fetch's result, when it found no bundle published for this version.
    public event Action? NotPublishedFound;

    public bool Started {
        get {
            lock (_lock) return _started;
        }
    }

    public bool Running => !_completion.IsCompleted;

    /// The fetch found no bundle published for this version. A network failure or an interrupted fetch never sets it.
    public bool NotPublished => _notPublished;

    /// Completes once the fetch has exited, however it ended; never faults.
    public Task Completion => _completion;

    /// Spawns the fetch the first time it is called; every later call does nothing.
    public void Start() {
        lock (_lock) {
            if (_started) return;
            _started = true;
            var interrupt = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _interrupt  = interrupt;
            _completion = Task.Run(() => RunAsync(interrupt.Token), CancellationToken.None);
        }
    }

    /// Asks a running fetch to stop.
    public void Cancel() {
        lock (_lock) {
            if (_interrupt is { IsCancellationRequested: false } interrupt) interrupt.Cancel();
        }
    }

    async Task RunAsync(CancellationToken interrupt) {
        var notPublished = false;
        try {
            var result = await cli().LocalFetchAsync(line => {
                if (line.Kind != ProcessStreamKind.Stdout || LocalSetupLine.Parse(line.Text) is not { } parsed) return;

                if (parsed.Event == "failed" && parsed.Root.Str("code") == LocalInstallFailureCode.BundleNotPublished.Wire()) notPublished = true;
                Line?.Invoke(parsed);
            }, interrupt).ConfigureAwait(false);
            if (result.ExitCode != 0 && !interrupt.IsCancellationRequested)
                Console.Error.WriteLine($"kcap app: fetching the local bundle ahead of the install stopped (exit {result.ExitCode}).");
            if ((notPublished || result.ExitCode == NotPublishedExit) && !interrupt.IsCancellationRequested) {
                _notPublished = true;
                NotPublishedFound?.Invoke();
            }
        } catch (OperationCanceledException) when (interrupt.IsCancellationRequested) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: fetching the local bundle ahead of the install failed: {ex.Message}");
        }
    }
}
