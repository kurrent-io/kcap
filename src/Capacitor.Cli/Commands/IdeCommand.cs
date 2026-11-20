using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Ide;
using Capacitor.Client;
using Capacitor.Client.Mutation;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Serves the session services to one client over stdin and stdout until the client closes stdin. It reads the
/// local daemon and the server and changes neither: a daemon that is not running is reported, never started.
/// </summary>
public sealed class IdeCommand(
        DaemonStore daemons, ProfileContext profiles, ICredentialSource credentials, ICapacitorHttpClient http,
        TimeProvider time, ConfigRoot config, LocalRoot localRoot) {
    /// <summary>How long shutdown waits for the server connection and the daemon client to close before exiting anyway.</summary>
    static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(3);

    static readonly Task<MutationOutcome> Refused =
        Task.FromResult<MutationOutcome>(new MutationOutcome.Refused("daemon_changes_refused", RecoverySurface.None));

    public async Task<int> HandleAsync() {
        var stdout = Console.OpenStandardOutput();
        // stdout carries protocol lines only, so anything else written to Console.Out lands on stderr.
        Console.SetOut(Console.Error);

        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        using var loop = new IdeRpcLoop(reader, writer);

        var daemon = DaemonClientService.CreateResolved(daemons, profiles.Resolution, time, (_, _) => Refused);
        var lane = new ServerConnectionService(profiles, credentials, time, localRoot);
        var sessions = new ServerSessionsService(
            lane, ServerSessionsService.HttpFetch(http, profiles), ServerSessionsService.HttpOwners(http, profiles), time,
            onUnauthorized: lane.ParkSignedOut);
        var repoIdentity = new RepoIdentityResolver();
        var machineId = new MachineId(config).ReadPersisted();
        var directory = new AgentDirectory(
            daemon, new NoRemoteAgents(), lane, repoIdentity, GitRepository.ResolveMainRepoRoot,
            machineId, profiles.Resolution.ServerUrl, time, sessions);
        var reconnect = new HostReconnect(daemon.Status, daemon.RestartLoopAsync, lane.Status, () => lane.RestartAsync(), Console.Error);
        var host = new IdeHost(
            daemon, lane, sessions, directory, repoIdentity, GitRepository.ResolveMainRepoRoot, machineId, reconnect.ReconnectAsync, time,
            CapacitorVersion.Current());

        try {
            host.Attach(loop);
            daemon.Start();
            lane.Start();

            await loop.RunAsync(CancellationToken.None);
        } finally {
            await CloseSteps.RunAsync([
                ("host", () => Close(host)),
                ("agent directory", () => Close(directory)),
                ("server sessions", () => Close(sessions)),
                ("server connection", lane.DisposeAsync),
                ("daemon client", daemon.DisposeAsync),
            ], CloseBudget, time, Console.Error);
        }

        return 0;
    }

    static ValueTask Close(IDisposable disposable) {
        disposable.Dispose();

        return ValueTask.CompletedTask;
    }
}
