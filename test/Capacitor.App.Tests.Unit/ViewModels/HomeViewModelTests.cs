using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// HomeViewModel's Harnesses/Sessions projections ObserveOn(RxSchedulers.MainThreadScheduler)
/// before touching bound state (same reason as MainWindowViewModel/TrayViewModel), so every test
/// here runs inside AvaloniaSession.WithImmediateRxScheduler and carries
/// [NotInParallel("AvaloniaSession")] — see MainWindowViewModelTests' identical header comment.
public partial class HomeViewModelTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// The daemon mints agent ids as Guid("N") — 32 hex digits — and a Started outcome carrying
    /// anything else is the "launched but unopenable" case, so every launch fixture here
    /// uses real-shaped ids.
    const string LaunchedId = "0123456789abcdef0123456789abcdef";
    sealed class RecordingLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public LaunchRequest? Last;
        public LaunchOutcome Next = new(true, LaunchedId, null);
        /// Runs while the call is in flight, before its outcome returns.
        public Action? During;

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Last = request;
            During?.Invoke();
            return Task.FromResult(Next);
        }
    }

    /// Scripted stand-in for RepoPathStore.GetSortedPathsAsync — the real store reads the
    /// developer's own ~/.config/kcap/repos.json, which no test may touch.
    static Func<Task<string[]>> Known(params string[] paths) => () => Task.FromResult(paths);

    /// Primed connected: StartCommand's canExecute gates on daemon + server both being up, so a
    /// fixture that launches must model the connected steady state.
    static HomeViewModel Build(out RecordingLaunchClient launch, out AppStateStore store, string statePath) {
        launch = new RecordingLaunchClient();
        store = new AppStateStore(statePath);
        var daemon = new FakeDaemonClientService();
        Connect(daemon);
        return new HomeViewModel(daemon, store, launch, Known(), TimeProvider.System);
    }

    static IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> Server(params (string V, string[] Slugs)[] e) =>
        e.ToDictionary(x => x.V, x => (IReadOnlyList<ModelChoice>) [.. x.Slugs.Select(s => new ModelChoice(s, s))], StringComparer.OrdinalIgnoreCase);

    static DaemonInfo Box(string owner, params string[] piModels) => new() {
        Name = "box", OwnerUserId = owner, Connected = true, SupportedVendors = ["pi", "claude"],
        VendorModels = new() { ["pi"] = [.. piModels.Select(m => new VendorModelOptionDto { Value = m, Label = m + " label" })] },
    };

    static AgentStatusDto Agent(string id, string? repoPath) => new(
        id, "agent", "claude", repoPath, "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null);

    static void Connect(FakeDaemonClientService daemon, string connection = "connected") {
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: connection));
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
    }

    static async Task WaitUntilAsync(Func<bool> condition, string what, int ms = 2000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    /// Holds the first claimed LoadAsync until Release. The held result preserves inline
    /// continuations for the late-adoption assertion; FirstLoadStarted acknowledges ownership.
    sealed class HeldAppStateStore(AppState state) : IAppStateStore {
        readonly TaskCompletionSource<AppState> _first = new();
        readonly TaskCompletionSource<bool> _firstLoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _loads;

        public Task FirstLoadStarted => _firstLoadStarted.Task;

        public Task<AppState> LoadAsync() {
            if (Interlocked.Increment(ref _loads) != 1) return Task.FromResult(state);
            _firstLoadStarted.SetResult(true);
            return _first.Task;
        }

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) => Task.FromResult(true);

        public void Release() => _first.SetResult(state);
    }
}
