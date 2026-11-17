using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A bare <see cref="AgentOrchestrator"/> over its own daemons directory, with the consent plumbing and
/// server connection it needs, and on request the <see cref="LocalControlServer"/> on top of it.
/// Disposing it tears down everything it built.
/// </summary>
sealed class OrchestratorFixture : IAsyncDisposable {
    const string SeedRepo = "/repo";

    int _serverStopped;

    /// <param name="notifyOrchestrator">Hands <see cref="Notifier"/> to the orchestrator, so agent mutations pulse it.</param>
    /// <param name="notifyConnection">Hands <see cref="Notifier"/> to the connection, so hub-state changes pulse it.</param>
    /// <param name="setConfigRoot">Sets <see cref="DaemonConfig.ConfigRoot"/> to <paramref name="configRoot"/>.</param>
    public OrchestratorFixture(
            TempConfigRoot                       configRoot,
            TempHome                             home,
            string                               name,
            string                               serverUrl          = "http://127.0.0.1:1",
            bool                                 notifyOrchestrator = false,
            bool                                 notifyConnection   = false,
            bool                                 setConfigRoot      = false,
            LaunchConsentPolicy?                 consentPolicy      = null,
            IHostApplicationLifetime?            lifetime           = null,
            Func<DaemonConfig, DaemonRecorder>?  recorder           = null,
            [CallerFilePath] string              callerFilePath     = ""
        ) {
        Daemons = new TempDaemonStore(callerFilePath);

        Config = new DaemonConfig {
            Name         = name,
            ServerUrl    = serverUrl,
            Store        = Daemons.Store,
            WorktreeRoot = Daemons.PathTo("worktrees"),
        };
        if (setConfigRoot) Config.ConfigRoot = configRoot.Root;

        var stateRoot = Daemons.Store.StateDirectory(name);
        ConsentStore = new LaunchConsentStore(stateRoot, NullLogger.Instance, TimeProvider.System);
        if (consentPolicy is not null) ConsentStore.TryReplace(consentPolicy, out _);
        Broker = new LaunchConsentBroker();
        Gate   = new LaunchConsentGate(ConsentStore, new LaunchConsentDecisionLog(stateRoot, NullLogger.Instance), Broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        Notifier   = new DaemonStatusNotifier();
        Lifetime   = lifetime ?? new AgentOrchestratorHarness.StubHostLifetime();
        Connection = new ServerConnection(
            Config, new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance,
            TimeProvider.System, notifyConnection ? Notifier : null);

        Orchestrator = new AgentOrchestrator(
            Config, configRoot.Root, TestHarnesses.Under(home), Connection,
            new WorktreeManager(Config, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System),
            new RepoMatcher(Config, NullLogger<RepoMatcher>.Instance, TimeProvider.System),
            new NoopPtyProcessFactory(), new StubHttpClientFactory(), new FixedCapacitorHttpClient(),
            AuthFixtures.NewTokenStore(configRoot.Root),
            new LocalPermissionBridge(Connection, NullLogger<LocalPermissionBridge>.Instance, EphemeralLoopbackPortSource.Instance, TimeProvider.System),
            new Dictionary<string, IHostedAgentLauncher>(), new Dictionary<string, IHostedAgentRuntimeFactory>(), Lifetime,
            NullLogger<AgentOrchestrator>.Instance, Gate, TimeProvider.System,
            statusNotifier: notifyOrchestrator ? Notifier : null,
            recorder: recorder?.Invoke(Config));

        StatusIpc = new DaemonStatusIpc(Config, Orchestrator, Connection, Notifier, TimeProvider.System);
    }

    public TempDaemonStore          Daemons      { get; }
    public DaemonConfig             Config       { get; }
    public LaunchConsentStore       ConsentStore { get; }
    public LaunchConsentBroker      Broker       { get; }
    public LaunchConsentGate        Gate         { get; }
    public DaemonStatusNotifier     Notifier     { get; }
    public IHostApplicationLifetime Lifetime     { get; }
    public ServerConnection         Connection   { get; }
    public AgentOrchestrator        Orchestrator { get; }
    public DaemonStatusIpc          StatusIpc    { get; }
    public LocalControlServer?      Server       { get; private set; }

    public string SockPath => Daemons.Store.SocketPath(Config.Name);

    /// <summary>Starts the control server and returns once it accepts a connection: Bind publishes the
    /// socket file before Listen, so the file alone does not mean a connect will succeed.</summary>
    public async Task<LocalControlServer> StartServerAsync(
            CancellationToken           ct,
            IRestartStrategy?           restartStrategy   = null,
            Action<RestartCoordinator>? configureRestart  = null
        ) {
        var restart = RestartCoordinator.ForTest(Daemons.Store, Config.Name, Config.Name, restartStrategy ?? new NoopRestartStrategy(), TimeProvider.System);
        configureRestart?.Invoke(restart);

        Server = new LocalControlServer(
            Config, Orchestrator, restart,
            new LaunchConsentIpc(Broker, ConsentStore, Config, NullLogger<LaunchConsentIpc>.Instance),
            new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance),
            StatusIpc,
            new DaemonSettingsIpc(Config, Orchestrator, Notifier, NullLogger<DaemonSettingsIpc>.Instance),
            TestFences.Ipc(Config, Orchestrator), TestShutdown.Ipc(Orchestrator, restart, Lifetime), TestAccountsIpc.Inert(),
            TestSteering.Switch(Config), NullLogger<LocalControlServer>.Instance);
        await Server.StartAsync(ct);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(SockPath)) {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"The control server never bound {SockPath}.");
            await Task.Delay(20, ct);
        }
        await using (await ControlSocket.ConnectAsync(SockPath, ct)) { }

        return Server;
    }

    /// <summary>Starts the server, runs <paramref name="body"/> under a 15-second budget and disposes
    /// this fixture, which it takes ownership of.</summary>
    public async Task ServeAsync(Func<OrchestratorFixture, CancellationToken, Task> body) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var self = this;
        await StartServerAsync(cts.Token);
        await Assert.That(File.Exists(SockPath)).IsTrue();
        await body(this, cts.Token);
    }

    /// <summary>Stops the server once; a test that stops it itself leaves disposal a no-op.</summary>
    public async Task StopServerAsync(CancellationToken ct) {
        if (Server is null || Interlocked.Exchange(ref _serverStopped, 1) != 0) return;
        await Server.StopAsync(ct);
    }

    /// <summary>A seeded agent's worktree is a placeholder path and its hub is unreachable, so the
    /// orchestrator's teardown would spend seconds per agent running git against nothing and reporting
    /// to no server. These tests pin what a live daemon shows, not how it stops; that teardown has its
    /// own suites.</summary>
    async Task ForgetSeededAgentsAsync() {
        foreach (var row in Orchestrator.SnapshotAgentsForStatus()) {
            if (Orchestrator.GetAgentForTest(row.Id) is not { Worktree: { Path: SeedRepo, SourceRepo: SeedRepo } } agent) continue;

            await agent.Runtime.DisposeAsync();
            Orchestrator.UnpublishAgentForTest(row.Id);
        }
    }

    public async ValueTask DisposeAsync() {
        try {
            await ForgetSeededAgentsAsync();
            await Orchestrator.DisposeAsync();
            await StopServerAsync(CancellationToken.None);
            Server?.Dispose();
            await Connection.DisposeAsync();
        } finally {
            Daemons.Dispose();
        }
    }
}
