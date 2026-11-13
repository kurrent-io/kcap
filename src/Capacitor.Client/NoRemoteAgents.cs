using System.Reactive.Linq;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.Client;

/// The no-server fallback for a composition root with no live remote lane (most tests, and any
/// caller that builds its own AgentDirectory without one): never reports a remote agent or
/// daemon, so the directory it feeds stays a pure mirror of the local daemon.
public sealed class NoRemoteAgents : IRemoteAgentsService {
    public IObservableCache<AgentInstanceDto, string> Agents { get; } =
        new SourceCache<AgentInstanceDto, string>(a => a.AgentId).AsObservableCache();
    public IObservable<IReadOnlyList<DaemonInfo>> Daemons { get; } = Observable.Return<IReadOnlyList<DaemonInfo>>([]);
    public IObservable<DaemonListing> Listing { get; } = Observable.Return(new DaemonListing(null, 0, false));
    public IObservable<IReadOnlyList<DaemonAccountsDto>> DaemonAccounts { get; } = Observable.Return<IReadOnlyList<DaemonAccountsDto>>([]);
    public IObservable<IReadOnlyList<DaemonAccountsDto>> DrawableDaemonAccounts { get; } = Observable.Return<IReadOnlyList<DaemonAccountsDto>>([]);
    public Task RefreshDaemonsAsync() => Task.CompletedTask;
    public IObservable<bool> Current { get; } = Observable.Return(false);
    public Task<SessionRenameResult> RenameAsync(string agentId, string sessionId, string title, CancellationToken ct) =>
        Task.FromResult(new SessionRenameResult(false, null, Capacitor.Cli.Core.LocalIpc.SessionRenameReasons.Unauthenticated));
}
