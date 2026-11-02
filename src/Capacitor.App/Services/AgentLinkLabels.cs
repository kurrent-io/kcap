using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DynamicData;

namespace Capacitor.App.Services;

/// Labels a link to one of the app server's agent pages with that agent's title, read from the rows
/// the app already holds. The title is read when the link is drawn; <see cref="Titled"/> announces
/// an agent whose title has just become known, so a link drawn before it registered can fill in.
/// Nothing announces a later rename or status change.
public sealed class AgentLinkLabels : IDisposable {
    readonly string? _serverUrl;
    readonly IObservableCache<AgentRow, string> _rows;
    readonly Subject<string> _titled = new();
    readonly IDisposable _subscription;

    public AgentLinkLabels(string? serverUrl, IObservableCache<AgentRow, string> rows, IScheduler scheduler) {
        _serverUrl = serverUrl;
        _rows = rows;
        _subscription = rows.Connect()
            .SelectMany(changes => changes.Where(BecameTitled).Select(change => AgentIds.Normalize(change.Current.Id)).OfType<string>())
            .ObserveOn(scheduler)
            .Subscribe(_titled);
    }

    /// Normalized agent ids, on the scheduler the labels were built with.
    public IObservable<string> Titled => _titled;

    /// Null for any link that is not to an agent page on the app's server.
    public AgentLinkLabel? For(string url) {
        if (AgentLinkOpener.AgentIdOf(url, _serverUrl) is not { } raw || AgentIds.Normalize(raw) is not { } id) return null;
        // The row a click opens, so the title never names a different agent than the one it opens.
        var title = HeldRows(_rows, raw).FirstOrDefault() is { } row && HasTitle(row) ? row.Title : null;
        return new AgentLinkLabel(id, title ?? $"agent {(id.Length > 8 ? id[..8] : id)}", title is not null);
    }

    /// The id a held agent row stores for this agent, whichever GUID spelling the caller used; null
    /// when the app holds no such agent.
    public static string? HeldIdOf(IObservableCache<AgentRow, string> rows, string agentId) =>
        HeldRows(rows, agentId).FirstOrDefault()?.Id;

    // A row keeps the spelling its source used, so rows match by normalized id; lane order is the
    // workspace's own preference, local before remote.
    static IEnumerable<AgentRow> HeldRows(IObservableCache<AgentRow, string> rows, string agentId) {
        var normalized = AgentIds.Normalize(agentId);
        return rows.Items
            .Where(row => IsAgentLane(row) && AgentIds.Normalize(row.Id) == normalized)
            .OrderBy(row => row.Origin switch { AgentOrigin.Local => 0, AgentOrigin.Pending => 1, _ => 2 });
    }

    // Server rows are keyed by session id, which an agent link does not carry.
    static bool IsAgentLane(AgentRow row) => row.Origin is AgentOrigin.Local or AgentOrigin.Pending or AgentOrigin.Remote;

    static bool BecameTitled(Change<AgentRow, string> change) =>
        IsAgentLane(change.Current)
        && HasTitle(change.Current)
        && change.Reason switch {
            ChangeReason.Add => true,
            ChangeReason.Update => !(change.Previous is { HasValue: true, Value: var previous } && HasTitle(previous)),
            _ => false,
        };

    static bool HasTitle(AgentRow row) => !string.IsNullOrWhiteSpace(row.Title);

    public void Dispose() {
        _subscription.Dispose();
        _titled.Dispose();
    }
}
