using System.Reactive.Subjects;
using Capacitor.Client;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.Tests.Helpers;

/// Scripted IRemoteAgentsService: a SourceCache the test edits directly plus a BehaviorSubject
/// per stream, for any suite that needs remote rows in an AgentDirectory.
public sealed class FakeRemoteAgents : IRemoteAgentsService, IDisposable {
    public SourceCache<AgentInstanceDto, string> Cache { get; } = new(a => a.AgentId);
    public BehaviorSubject<IReadOnlyList<DaemonInfo>> DaemonsSubject { get; } = new([]);
    public BehaviorSubject<DaemonListing> ListingSubject { get; } = new(new(null, 0, false));
    public BehaviorSubject<IReadOnlyList<DaemonAccountsDto>> DaemonAccountsSubject { get; } = new([]);
    public BehaviorSubject<IReadOnlyList<DaemonAccountsDto>> DrawableDaemonAccountsSubject { get; } = new([]);
    /// True unless a test is about retained rows: the cache a test edits is what the registry says now.
    public BehaviorSubject<bool> CurrentSubject { get; } = new(true);
    public IObservableCache<AgentInstanceDto, string> Agents => Cache.AsObservableCache();
    public IObservable<IReadOnlyList<DaemonInfo>> Daemons => DaemonsSubject;
    public IObservable<DaemonListing> Listing => ListingSubject;
    public IObservable<IReadOnlyList<DaemonAccountsDto>> DaemonAccounts => DaemonAccountsSubject;
    public IObservable<IReadOnlyList<DaemonAccountsDto>> DrawableDaemonAccounts => DrawableDaemonAccountsSubject;
    public IObservable<bool> Current => CurrentSubject;
    public Task RefreshDaemonsAsync() => Task.CompletedTask;
    public Func<string, string, string, Task<SessionRenameResult>> Rename { get; set; } =
        (_, _, _) => Task.FromResult(new SessionRenameResult(false, null, "unauthenticated"));
    public Task<SessionRenameResult> RenameAsync(string agentId, string sessionId, string title, CancellationToken ct) =>
        Rename(agentId, sessionId, title);
    public void Dispose() => Cache.Dispose();
}
