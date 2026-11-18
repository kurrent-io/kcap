using System.Collections.Frozen;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

public interface IAgentDirectory {
    IObservableCache<AgentRow, string> Rows { get; }
    /// Whether a daemon lane currently vouches for the row: a live status under a connected local
    /// socket on the app's server, or under a current registry. A retained row is history on
    /// display, and a server row never qualifies.
    bool IsHostedNow(AgentRow row);
    /// True while the server lane is not Connected — rail rows grey out on it.
    IObservable<bool> RemoteStale { get; }
    /// True while the local daemon reports the app's own server; replays on subscribe. A session
    /// id is unique only within one server, so nothing local may be matched to a server-lane
    /// session while this is false: the same id there names a different session.
    IObservable<bool> LocalDaemonOnAppServer { get; }
    /// Session id -> logical agent id, over the current rows. Replay-1, distinct by content.
    IObservable<IReadOnlyDictionary<string, string>> SessionAgents { get; }
    /// The vendor of the row whose SessionId matches, or null.
    string? VendorOfSession(string sessionId);
    /// The row a rail click on this server-lane session id opens: a daemon row that survived the
    /// merge (local first), else its server row, else null.
    (string Id, AgentOrigin Origin)? RowForSession(string sessionId);
    /// Whether the local daemon has proven it hosts this agent — the daemon proved to be its
    /// server twin registers it. A shared agent id proves nothing: the dedup fails open.
    bool IsProvenLocalTwin(string agentId);
    /// The app saw this session's hosted agent end, which the server's listing can trail: its row
    /// reads ended until a daemon row hosts the session again.
    void NoteFinished(string sessionId) { }
    /// A row for a launch the server accepted, standing until the local lane publishes the id;
    /// a same-id row on the remote lane is a different agent and leaves it in place. It is
    /// dropped by the caller on a launch failure, and expires on its own after ten minutes.
    void AddPlaceholder(string agentId, string vendor, string repoPath, string? title, string? model);
    /// The remote-lane counterpart, standing until the registry lists the id in any status. Keyed
    /// by the id as given, which should be the server's own spelling so the real row replaces it.
    void AddRemotePlaceholder(
        string agentId, string machine, string? ownerUserId, string vendor, string repoPath, string? title, string? model);
    /// Drops the launch's stand-in on either lane.
    void RemovePlaceholder(string agentId);
}

/// Merges the local daemon's agents with the server registry's into source-scoped rows.
/// Suppression is evidence-based, symmetric and pairwise — a row hides only where the twin daemon's
/// other lane holds a row for the SAME agent. While the twin is proven, its remote row hides
/// whenever the local socket is Connected (the local view is current), and the local row hides once
/// it isn't (the twin's own server row wins) — except for an agent the daemon's own resume ledger
/// says it is taking back up while its process starts, whose local row stands as Resuming, and an
/// attached agent, whose twin's row always stands because only the server can send to it. Another
/// daemon's rows never take part: an unproven identity, an unpaired agent and a same-id agent
/// elsewhere all keep both lanes' rows.
public sealed class AgentDirectory : IAgentDirectory, IDisposable {
    readonly SourceCache<AgentRow, string> _rows = new(r => r.Key);
    readonly CompositeDisposable _subscriptions = new();
    readonly IDaemonClientService _local;
    readonly RepoIdentityResolver _repoIdentity;
    readonly Func<string, string> _resolveLocalRepoRoot;
    readonly string? _localMachineId;
    readonly string? _appServerUrl;
    readonly TimeProvider _time;
    // Nothing else need ever change in an idle directory, so expiry is this timer's job alone.
    readonly ITimer _placeholderExpiry;
    /// How long a server row keeps looking hosted after the daemon row for its session went away.
    public static readonly TimeSpan HandoverGrace = TimeSpan.FromSeconds(30);
    /// How long a finished agent's session shows ended while the listing still calls it active. The server
    /// folds the end 10–30 s after the stop and the listing re-reads in full every 60 s, so past this the
    /// listing's own answer stands, and a session resumed outside the daemon reads active again.
    public static readonly TimeSpan FinishedHold = TimeSpan.FromSeconds(90);
    readonly ITimer _graceExpiry;
    readonly IServerSessionsService? _sessions;
    readonly object _lock = new();
    readonly BehaviorSubject<bool> _onAppServer = new(false);

    IReadOnlyList<DaemonInfo> _daemons = [];
    (string OwnerUserId, string DaemonName)? _provenTwin;
    bool _localConnected;
    bool _localStarting;
    /// The reason the local daemon last reported Unreachable since it was last Connected.
    string? _localOutage;
    IReadOnlySet<string> _resuming = FrozenSet<string>.Empty;
    string? _localServerUrl;
    List<AgentInstanceDto> _remoteAgents = [];
    List<AgentStatusDto> _localAgents = [];
    List<PendingLaunchDto> _pendingLaunches = [];
    readonly Dictionary<string, AgentRow> _placeholders = new(StringComparer.Ordinal);
    readonly Dictionary<string, AgentRow> _remotePlaceholders = new(StringComparer.Ordinal);
    static readonly TimeSpan PlaceholderTtl = TimeSpan.FromMinutes(10);
    const string DaemonUnreachableReason = "daemon_unreachable";
    FrozenSet<string> _twinAgents = FrozenSet<string>.Empty;
    bool _registryCurrent;
    List<RepoSessionDto> _serverLive = [];
    List<RepoSessionDto> _serverMineLive = [];
    List<RepoSessionDto> _serverEnded = [];
    RepoSessionDto? _serverPinned;
    string? _serverPinnedId;
    RailScope _scope = RailScope.Mine;
    IReadOnlySet<string> _viewerIds = FrozenSet<string>.Empty;
    // Aliases receive the same checkout snapshot, but presentation leases belong to the exact
    // listing key. Server ids remain distinct even when a daemon can match either spelling.
    readonly Dictionary<string, SessionMergeState> _sessionStates = new(StringComparer.Ordinal);
    readonly HashSet<string> _timedSessions = new(StringComparer.Ordinal);
    bool _disposed;

    /// <param name="localStarting">The local daemon's process is up but not yet serving its socket.</param>
    /// <param name="resumingAgents">The agent ids the local daemon's resume ledger holds as resuming.</param>
    public AgentDirectory(
            IDaemonClientService local, IRemoteAgentsService remote, IServerLane lane,
            RepoIdentityResolver repoIdentity, Func<string, string> resolveLocalRepoRoot,
            string? localMachineId, string? appServerUrl, TimeProvider time,
            IServerSessionsService? sessions = null,
            IObservable<bool>? localStarting = null, Func<IReadOnlySet<string>>? resumingAgents = null) {
        _local = local;
        _repoIdentity = repoIdentity;
        _resolveLocalRepoRoot = resolveLocalRepoRoot;
        _localMachineId = localMachineId;
        _appServerUrl = appServerUrl;
        _time = time;
        _sessions = sessions;
        _placeholderExpiry = _time.CreateTimer(_ => Recompute(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _graceExpiry = _time.CreateTimer(_ => Recompute(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        remote.Current
            .Subscribe(current => { lock (_lock) _registryCurrent = current; Recompute(); })
            .DisposeWith(_subscriptions);
        if (sessions is not null) {
            // One subscription for all three sets: a recompute between two of them would judge a
            // live row against an ownership set from the previous read.
            sessions.Snapshots
                .Subscribe(snapshot => {
                    lock (_lock) {
                        _serverLive = [.. snapshot.Live];
                        _serverMineLive = [.. snapshot.MineLive];
                        _serverEnded = [.. snapshot.Ended];
                        _serverPinned = snapshot.Pinned;
                        _serverPinnedId = snapshot.PinnedSessionId;
                    }
                    Recompute();
                })
                .DisposeWith(_subscriptions);
            sessions.ScopeChanges
                .Subscribe(scope => { lock (_lock) _scope = scope; Recompute(); })
                .DisposeWith(_subscriptions);
            sessions.ViewerIds
                .Subscribe(ids => { lock (_lock) _viewerIds = ids; Recompute(); })
                .DisposeWith(_subscriptions);
        }

        RemoteStale = lane.Status.Select(s => s.State != ServerLaneState.Connected).DistinctUntilChanged();

        // ToCollection (not the raw changeset), matching remote.Agents below: whether a local row
        // shows depends on the whole remote set, so Recompute needs the full current local set on
        // every change — an incremental Add/Update/Remove-per-key handler can't express that.
        local.Agents.Connect().ToCollection()
            .Subscribe(items => { lock (_lock) _localAgents = [.. items]; Recompute(); })
            .DisposeWith(_subscriptions);

        local.Pending.Connect().ToCollection()
            .Subscribe(items => { lock (_lock) _pendingLaunches = [.. items]; Recompute(); })
            .DisposeWith(_subscriptions);

        remote.Agents.Connect().ToCollection()
            .Subscribe(items => { lock (_lock) _remoteAgents = [.. items]; Recompute(); })
            .DisposeWith(_subscriptions);
        remote.Daemons
            .Subscribe(d => { lock (_lock) _daemons = d; Recompute(); })
            .DisposeWith(_subscriptions);
        // Subscribed before Status so a replayed snapshot's connected flag is overridden when
        // Status is currently Unreachable; on a live Connected event, Snapshots arrives ahead of
        // Status (DaemonClientService.Apply) and marks _localConnected before Recompute runs, so
        // remote twin rows do not flash while _localServerUrl is set and _localConnected is false.
        local.Snapshots
            .Subscribe(s => {
                lock (_lock) {
                    if (_localServerUrl == s.Daemon.ServerUrl && _localConnected) return;
                    _localServerUrl = s.Daemon.ServerUrl;
                    _localConnected = true;
                }
                Recompute();
            })
            .DisposeWith(_subscriptions);
        local.Status
            .Subscribe(s => {
                lock (_lock) {
                    var connected = s.State == AttachState.Connected;
                    // A reconnect attempt's Connecting continues the outage the last Unreachable reported.
                    var outage = s.State switch {
                        AttachState.Connected => null,
                        AttachState.Unreachable => s.Reason,
                        _ => _localOutage,
                    };
                    if (_localConnected == connected && _localOutage == outage) return;
                    _localConnected = connected;
                    _localOutage = outage;
                }
                Recompute();
            })
            .DisposeWith(_subscriptions);
        // The ledger is read once as the start begins: it changes only as the daemon resumes, and from its
        // first snapshot on the daemon says so itself.
        localStarting?
            .DistinctUntilChanged()
            .Subscribe(starting => {
                var resuming = starting ? ReadResuming(resumingAgents) : FrozenSet<string>.Empty;
                lock (_lock) {
                    _localStarting = starting;
                    _resuming = resuming;
                }
                Recompute();
            })
            .DisposeWith(_subscriptions);
    }

    static IReadOnlySet<string> ReadResuming(Func<IReadOnlySet<string>>? read) {
        if (read is null) return FrozenSet<string>.Empty;
        try {
            return read();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: reading the daemon's resume ledger failed: {ex.Message}");
            return FrozenSet<string>.Empty;
        }
    }

    public IObservableCache<AgentRow, string> Rows => _rows.AsObservableCache();
    public IObservable<bool> RemoteStale { get; }
    public IObservable<bool> LocalDaemonOnAppServer => _onAppServer;

    public bool IsHostedNow(AgentRow row) {
        if (ViewModels.SessionStatusDots.IsTerminal(row.Status)) return false;
        lock (_lock) {
            return row.Origin switch {
                AgentOrigin.Local => _localConnected && _onAppServer.Value,
                AgentOrigin.Remote => _registryCurrent && row.Status != AgentRow.DaemonDownStatus,
                _ => false,
            };
        }
    }

    // The scope flag is an input, not just a consumer's filter: a flip changes the answer for rows
    // that never moved, so the map has to be republished on it.
    public IObservable<IReadOnlyDictionary<string, string>> SessionAgents => _rows.Connect()
        .QueryWhenChanged(q => (IReadOnlyList<AgentRow>)[.. q.Items])
        .StartWith((IReadOnlyList<AgentRow>)[.. _rows.Items])
        .CombineLatest(_onAppServer, SessionMap)
        .DistinctUntilChanged(new DictionaryEquality());

    public string? VendorOfSession(string sessionId) =>
        ServerSessionRows(_rows.Items, _onAppServer.Value)
            .Where(r => r.SessionId == sessionId).OrderBy(r => r.Origin).Select(r => r.Vendor).FirstOrDefault();

    public (string Id, AgentOrigin Origin)? RowForSession(string sessionId) {
        var rows = _rows.Items.ToList();
        var daemon = ServerSessionRows(rows, _onAppServer.Value)
            .Where(r => SessionIds.Canonical(r.SessionId) is { } own && (own == sessionId || SessionIds.ServerToken(own) == sessionId))
            .OrderBy(r => r.Origin)
            .FirstOrDefault();
        if (daemon is not null) return (daemon.Id, daemon.Origin);
        return rows.FirstOrDefault(r => r.Key == $"server:{sessionId}") is { } server ? (server.Id, server.Origin) : null;
    }

    public bool IsProvenLocalTwin(string agentId) => _twinAgents.Contains(agentId);

    public void NoteFinished(string sessionId) {
        lock (_lock) MarkFinished(sessionId);
        Recompute();
    }

    void MarkFinished(string? sessionId) {
        if (SessionIds.Canonical(sessionId) is not { } own) return;
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var id in new[] { own, SessionIds.ServerToken(own) }) {
            var (state, _) = StateFor(id).Transition([], null, false, false, now, observedFinished: true);
            StoreState(id, state);
        }
    }

    SessionMergeState StateFor(string id) => _sessionStates.GetValueOrDefault(id) ?? SessionMergeState.Empty;

    void StoreState(string id, SessionMergeState state) {
        if (state.HasRetention) _sessionStates[id] = state;
        else _sessionStates.Remove(id);
        if (state.NextExpiry is not null) _timedSessions.Add(id);
        else _timedSessions.Remove(id);
    }

    // Placeholders are keyed by the normalized id: the server accepts a launch under one spelling
    // and the daemon can publish it under another.
    public void AddPlaceholder(string agentId, string vendor, string repoPath, string? title, string? model) {
        if (AgentIds.Normalize(agentId) is not { } key) return;
        lock (_lock) _placeholders[key] = AgentRow.Placeholder(key, vendor, repoPath, title, model, _time.GetUtcNow().UtcDateTime, RepoFor(repoPath));
        Recompute();
    }

    public void AddRemotePlaceholder(
            string agentId, string machine, string? ownerUserId, string vendor, string repoPath, string? title, string? model) {
        if (AgentIds.Normalize(agentId) is not { } key) return;
        lock (_lock) {
            var daemonKey = $"{ownerUserId}/{machine}";
            // The registry names the repository by its remote, which only the target machine knows;
            // a row it already lists for the same checkout keeps the stand-in in that row's group.
            var sibling = _remoteAgents.FirstOrDefault(a =>
                a.DaemonName == machine && a.OwnerUserId == ownerUserId && a.RepoPath == repoPath);
            var repo = RepoIdentityResolver.ForRemote(sibling?.RepoOwner, sibling?.RepoName, repoPath, daemonKey);
            _remotePlaceholders[key] = AgentRow.RemotePlaceholder(
                agentId, machine, vendor, repoPath, title, model, _time.GetUtcNow().UtcDateTime, repo, daemonKey);
        }
        Recompute();
    }

    public void RemovePlaceholder(string agentId) {
        if (AgentIds.Normalize(agentId) is not { } key) return;
        lock (_lock) {
            _placeholders.Remove(key);
            _remotePlaceholders.Remove(key);
        }
        Recompute();
    }

    /// Both session lookups answer for a server-lane session id. While the local daemon reports
    /// another server, its rows carry that server's ids and a match here is coincidence: the id
    /// names a different session, whose agent is not the local one.
    /// A server row is left out: nothing here can answer for a session no daemon hosts.
    static IEnumerable<AgentRow> ServerSessionRows(IEnumerable<AgentRow> rows, bool localOnAppServer) =>
        rows.Where(r => r.Origin != AgentOrigin.Server && (localOnAppServer || r.Origin != AgentOrigin.Local));

    // Local sorts before Remote in AgentOrigin, so the ordered pass's TryAdd lets a local row win
    // a session claimed by both an unproven twin pair — proven suppression already keeps a twin's
    // remote row out of _rows entirely, so this tie only ever arises while the pairing is unproven.
    static IReadOnlyDictionary<string, string> SessionMap(IReadOnlyList<AgentRow> rows, bool localOnAppServer) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in ServerSessionRows(rows, localOnAppServer).OrderBy(r => r.Origin))
            if (row.SessionId is { Length: > 0 } sid) map.TryAdd(sid, row.Id);
        return map.Count == 0 ? FrozenDictionary<string, string>.Empty : map;
    }

    sealed class DictionaryEquality : IEqualityComparer<IReadOnlyDictionary<string, string>> {
        public bool Equals(IReadOnlyDictionary<string, string>? x, IReadOnlyDictionary<string, string>? y) =>
            x is not null && y is not null && x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && v == kv.Value);
        public int GetHashCode(IReadOnlyDictionary<string, string> obj) => obj.Count;
    }

    readonly Dictionary<string, RepoIdentity> _repoByPath = new(StringComparer.Ordinal);

    RepoIdentity RepoFor(string? repoPath) {
        if (repoPath is not { Length: > 0 } path) return RepoIdentity.NoRepository;
        if (_repoByPath.TryGetValue(path, out var cached)) return cached;
        var resolved = _repoIdentity.ForLocalRoot(PlatformPaths.Normalize(_resolveLocalRepoRoot(path)));
        _repoByPath[path] = resolved;
        return resolved;
    }

    AgentRow ProjectLocal(AgentStatusDto dto) => AgentRow.FromLocal(dto, RepoFor(dto.RepoPath));

    // The compute-then-edit pair must be one atomic unit under _lock: two triggers (e.g. a
    // socket-thread Status flip racing a SignalR-thread Daemons refresh) that read-then-edit as
    // separate critical sections can land their _rows.Edit calls out of read order, letting a
    // stale edit overwrite a fresher one. DynamicData's own cache lock is separate, so nesting
    // _rows.Edit inside _lock is deadlock-free. Owns BOTH lanes' rows in one pass, because
    // precedence is pairwise: while the twin is proven and the local socket is down, a local row
    // yields to the twin's own row for the SAME agent — but only to that row. Absent server data is
    // not evidence an agent ended (a private agent is never registered, and the registry has a
    // seed gap after every connect), so an unpaired local row stands as display-only history.
    void Recompute() {
        lock (_lock) {
            // The expiry timer's callback can land after Dispose ran; the lock serialises the two
            // and the flag turns the late callback into a no-op instead of an edit of a disposed cache.
            if (_disposed) return;
            // A daemon that drops off the server's registry while it restarts is the same daemon: the
            // identity proven before stands until the registry gives its slot to another machine.
            var found = LocalDaemonTwin.Find(_daemons, _localMachineId, _local.DaemonName, _localServerUrl, _appServerUrl);
            _provenTwin = found ?? LocalDaemonTwin.Retained(_provenTwin, _daemons, _localMachineId, _local.DaemonName, _localServerUrl, _appServerUrl);
            var twin = _provenTwin;
            var twinProven = twin is not null;
            bool OnTwin(AgentInstanceDto a) =>
                twinProven && a.OwnerUserId == twin!.Value.OwnerUserId && a.DaemonName == twin.Value.DaemonName;

            // While the daemon's process starts and its socket is not yet up, its own resume ledger is
            // the evidence that it takes an agent back up: the row it last reported stands for that
            // agent, as Resuming.
            var resumingHere = !_localConnected && _localStarting
                ? _localAgents.Select(a => a.Id).Where(_resuming.Contains).ToHashSet(StringComparer.Ordinal)
                : [];

            // What says a retiring remote row means the local daemon took that agent over. It is a
            // takeover verdict, so it takes current local authority and not the pairing alone: the
            // socket up and the agent still live on it, or the daemon's ledger resuming it. A server
            // verdict that ends the session retires the same row, and read as a takeover it would hide the end.
            var claimedLocally = _localConnected
                ? _localAgents.Where(a => !ViewModels.SessionStatusDots.IsTerminal(a.Status))
                    .Select(a => a.Id).ToHashSet(StringComparer.Ordinal)
                : resumingHere;
            // An attached agent is never taken over: the daemon steers it, it never hosts it.
            _twinAgents = twinProven
                ? _remoteAgents.Where(a => OnTwin(a) && !a.Attached).Select(a => a.AgentId).Where(claimedLocally.Contains).ToFrozenSet(StringComparer.Ordinal)
                : FrozenSet<string>.Empty;

            var remote = _remoteAgents
                // The server registry uses lowercase `idle` for a live agent waiting between
                // turns; local daemon snapshots call that same live state `Running`.
                .Where(a => a.Status.Equals("Starting", StringComparison.OrdinalIgnoreCase)
                    || a.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)
                    || a.Status.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                // Only the server can send to an attached agent, so its twin's registry row stands for it even while
                // the local socket is up.
                .Where(a => a.Attached || !(OnTwin(a) && (_localConnected || resumingHere.Contains(a.AgentId))))
                .ToList();
            // The counterpart set is the TWIN's own rows, never every remote row: proving this
            // daemon's registry twin establishes no correspondence with an agent of the same id
            // running on some other daemon, which is a different agent entirely.
            var twinIds = remote.Where(OnTwin).Select(a => a.AgentId).ToHashSet(StringComparer.Ordinal);
            var localRows = _localAgents
                .Where(a => !(twinProven && twinIds.Contains(a.Id) && (!_localConnected || a.Kind == AgentActionService.AttachedKind)))
                .Select(a => ProjectLocal(resumingHere.Contains(a.Id) ? a with { Status = "Resuming" } : a));
            // A twin row standing in for a hidden local one keeps the checkout that daemon reported for the
            // same agent: the registry names none, and the session pane would lose its worktree.
            var hidden = _localConnected
                ? FrozenDictionary<string, AgentStatusDto>.Empty
                : _localAgents.Where(a => twinIds.Contains(a.Id)).ToFrozenDictionary(a => a.Id, StringComparer.Ordinal);
            // The registry lists a dead daemon's agents live until the server notices the disconnect. Only a
            // daemon that cannot be reached is down: one the app rejects as incompatible still serves the server.
            var twinDown = twinProven && !_localConnected && !_localStarting && _localOutage == DaemonUnreachableReason;
            var remoteRows = remote.Select(a => {
                var row = AgentRow.FromRemote(a);
                if (twinDown && OnTwin(a)) row = row with { Status = AgentRow.DaemonDownStatus };
                return OnTwin(a) && hidden.TryGetValue(a.AgentId, out var own)
                    ? row with { WorktreePath = own.WorktreePath, WorkLocation = own.WorkLocation, BorrowedFrom = own.BorrowedFrom }
                    : row;
            });
            var next = localRows.Concat(remoteRows).ToList();
            if (_sessions is not null) MergeServerLane(next);

            // A published local row retires the launch's stand-ins for good; the daemon's own
            // pending entry only hides the app's placeholder, which returns should the entry vanish
            // without a row, until the failure notice or the TTL removes it. Only the local lane
            // counts: a same-id row on the remote lane is a different agent, and ids compare in
            // their normalized form because the two lanes spell a Guid differently.
            var published = next.Where(r => r.Origin == AgentOrigin.Local).Select(r => AgentIds.Normalize(r.Id)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var id in _placeholders.Keys.Where(published.Contains).ToList()) _placeholders.Remove(id);
            // Any registry row retires a remote stand-in, a failed one included: from then on the
            // registry speaks for the launch.
            var registered = _remoteAgents.Select(a => AgentIds.Normalize(a.AgentId)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var id in _remotePlaceholders.Keys.Where(registered.Contains).ToList()) _remotePlaceholders.Remove(id);
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var id in _placeholders.Where(kv => kv.Value.CreatedAt + PlaceholderTtl <= now).Select(kv => kv.Key).ToList()) _placeholders.Remove(id);
            foreach (var id in _remotePlaceholders.Where(kv => kv.Value.CreatedAt + PlaceholderTtl <= now).Select(kv => kv.Key).ToList()) _remotePlaceholders.Remove(id);
            var standIns = _placeholders.Values.Concat(_remotePlaceholders.Values).ToList();
            // Infinite when nothing is pending; otherwise positive, since every expired entry was just removed.
            var nextExpiry = standIns.Count == 0 ? Timeout.InfiniteTimeSpan : standIns.Min(r => r.CreatedAt) + PlaceholderTtl - now;
            _placeholderExpiry.Change(nextExpiry, Timeout.InfiniteTimeSpan);
            var pendingRows = _pendingLaunches
                .Where(p => AgentIds.Normalize(p.Id) is { } id && !published.Contains(id))
                .Select(p => AgentRow.FromPending(p, RepoFor(p.RepoPath)))
                .ToList();
            var starting = pendingRows.Select(r => AgentIds.Normalize(r.Id)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            next.AddRange(pendingRows);
            next.AddRange(_placeholders.Values.Where(r => !starting.Contains(r.Id)));
            next.AddRange(_remotePlaceholders.Values);

            var nextKeys = next.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
            _rows.Edit(cache => {
                foreach (var key in cache.Keys.Where(k => !nextKeys.Contains(k)).ToList())
                    cache.RemoveKey(key);
                foreach (var row in next)
                    if (cache.Lookup(row.Key) is not { HasValue: true, Value: var existing } || existing != row)
                        cache.AddOrUpdate(row);
            });

            // Published inside the lock, like the row edit above, so no subscriber sees this
            // verdict and the rows disagreeing about which recompute produced them. A side that
            // does not canonicalize is not a match: no assertion can be made, and matching a local
            // session to a server one on that basis is what this exists to prevent.
            var onAppServer = ServerIdentity.SameServer(_localServerUrl, _appServerUrl);
            if (_onAppServer.Value != onAppServer) _onAppServer.OnNext(onAppServer);
        }
    }

    void MergeServerLane(List<AgentRow> next) {
        var now = _time.GetUtcNow().UtcDateTime;
        var localMatchable = ServerIdentity.SameServer(_localServerUrl, _appServerUrl);
        var registryMatchable = _appServerUrl is not null;
        var listed = new Dictionary<string, RepoSessionDto>(StringComparer.Ordinal);
        foreach (var dto in _serverLive) listed[dto.SessionId] = dto;
        foreach (var dto in _serverMineLive) listed.TryAdd(dto.SessionId, dto);
        foreach (var dto in _serverEnded) listed.TryAdd(dto.SessionId, dto);
        if (_serverPinned is { } pinned) listed.TryAdd(pinned.SessionId, pinned);
        var mine = _serverMineLive.Select(dto => dto.SessionId).ToHashSet(StringComparer.Ordinal);
        if (_scope == RailScope.Mine) foreach (var dto in _serverEnded) mine.Add(dto.SessionId);

        bool? Ownership(RepoSessionDto dto, string sid) =>
            sid == _serverPinnedId && !mine.Contains(sid) ? ViewerOwns(dto) : OwnerIsViewer(dto, mine.Contains(sid));
        bool Matchable(AgentRow row) => row.Origin == AgentOrigin.Local ? localMatchable : registryMatchable;
        string? ListedId(AgentRow row) {
            if (SessionIds.Canonical(row.SessionId) is not { } own) return null;
            if (listed.ContainsKey(own)) return own;
            var token = SessionIds.ServerToken(own);
            return listed.ContainsKey(token) ? token : null;
        }
        var listedIdOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in next) if (row.Origin is AgentOrigin.Local or AgentOrigin.Remote) listedIdOf[row.Key] = ListedId(row);
        if (localMatchable)
            foreach (var row in next)
                if (row is { Origin: AgentOrigin.Local, Status: "Completed" }) MarkFinished(row.SessionId);

        // Capture before visibility filtering: a run can finish before its first listing,
        // and a session hidden by scope can become visible again with the same checkout.
        foreach (var row in next) {
            if (row.Origin is not (AgentOrigin.Local or AgentOrigin.Remote) || !Matchable(row)) continue;
            if (row.RepoPath is null && row.WorktreePath is null || SessionIds.Canonical(row.SessionId) is not { } own) continue;
            var metadata = listedIdOf.GetValueOrDefault(row.Key) is { } listedSid ? listed.GetValueOrDefault(listedSid) : null;
            var daemonBranch = row.Origin == AgentOrigin.Local ? _localAgents.FirstOrDefault(a => a.Id == row.Id)?.Branch : null;
            var checkout = HostedSessionCheckout.Capture(StateFor(own).Checkout, row, daemonBranch, metadata?.Branch);
            foreach (var id in new[] { own, SessionIds.ServerToken(own) })
                StoreState(id, StateFor(id) with { Checkout = checkout });
        }

        if (_scope == RailScope.Mine) {
            next.RemoveAll(row => {
                if (row.Origin is not (AgentOrigin.Local or AgentOrigin.Remote) || !Matchable(row)) return false;
                if (listedIdOf.GetValueOrDefault(row.Key) is { } sid) return !mine.Contains(sid) && sid != _serverPinnedId;
                return row.Origin == AgentOrigin.Remote && !(row.OwnerUserId is { } owner && _viewerIds.Contains(owner));
            });
        }

        // Metadata prefers the live listing above; emitted server rows use the ended set's
        // last word. A pin fills a missing entry without overriding either source.
        var source = (_scope == RailScope.Mine ? _serverMineLive : _serverLive).Concat(_serverEnded);
        var byId = new Dictionary<string, RepoSessionDto>(StringComparer.Ordinal);
        foreach (var dto in source) byId[dto.SessionId] = dto;
        if (_serverPinnedId is { } pinnedId && listed.TryGetValue(pinnedId, out var pinnedDto)) byId.TryAdd(pinnedId, pinnedDto);
        var sessionOfRow = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in next)
            if ((row.Origin is AgentOrigin.Local or AgentOrigin.Remote) && Matchable(row)
                && (listedIdOf.GetValueOrDefault(row.Key) ?? SessionIds.Canonical(row.SessionId)) is { } sid)
                sessionOfRow[row.Key] = sid;
        var candidates = next.Where(row => sessionOfRow.ContainsKey(row.Key))
            .GroupBy(row => sessionOfRow[row.Key], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AgentRow>)group.ToList(), StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        var serverRows = new List<AgentRow>();
        foreach (var sid in byId.Keys.Concat(candidates.Keys.Where(id => !byId.ContainsKey(id)))) {
            var rows = candidates.GetValueOrDefault(sid) ?? [];
            var listing = byId.GetValueOrDefault(sid);
            var (state, keptRows) = StateFor(sid).Transition(rows, listing, _localConnected, _registryCurrent, now);
            StoreState(sid, state);
            foreach (var row in rows) if (!keptRows.Contains(row.Key)) removed.Add(row.Key);
            if (listing is not null && keptRows.Count == 0) serverRows.Add(state.ProjectServer(listing, Ownership(listing, sid), now));
        }

        // Keep daemon order and keys, including unproven duplicates; resolution changes
        // their fields and visibility, never their source identity.
        for (var i = next.Count - 1; i >= 0; i--) {
            var row = next[i];
            if (removed.Contains(row.Key)) { next.RemoveAt(i); continue; }
            if (!Matchable(row) || listedIdOf.GetValueOrDefault(row.Key) is not { } sid || !listed.TryGetValue(sid, out var metadata)) continue;
            next[i] = StateFor(sid).ProjectDaemon(row, metadata, Ownership(metadata, sid));
        }
        next.AddRange(serverRows);

        foreach (var sid in _timedSessions.ToArray()) StoreState(sid, StateFor(sid).Expire(now));
        var lapses = _timedSessions.Select(id => StateFor(id).NextExpiry.GetValueOrDefault()).ToList();
        _graceExpiry.Change(lapses.Count == 0 ? Timeout.InfiniteTimeSpan : lapses.Min() - now, Timeout.InfiniteTimeSpan);
    }

    /// Under Mine the scoped listing answers; under Team only the account's ids can, and only once known.
    bool? OwnerIsViewer(RepoSessionDto dto, bool inMine) => _scope == RailScope.Mine ? inMine : ViewerOwns(dto);

    bool? ViewerOwns(RepoSessionDto dto) =>
        _viewerIds.Count > 0 && dto.Owner is { } owner ? _viewerIds.Contains(owner.UserId) : null;

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            _placeholderExpiry.Dispose();
            _graceExpiry.Dispose();
        }
        _subscriptions.Dispose();
        _rows.Dispose();
    }
}
