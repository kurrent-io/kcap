using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Client;
using DynamicData;

namespace Capacitor.Cli.Ide;

/// <summary>
/// Serves the session services to one client: each subscription gets a snapshot of the rows under its git roots,
/// then batched changes to those rows only. Every notification leaves through one queue, in the order it was
/// decided under the lock, so a subscription's snapshot always goes out before its changes.
/// </summary>
internal sealed class IdeHost : IDisposable {
    const int ProtocolVersion = 1;
    const string Capability = "sessions/1";
    static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(100);

    readonly IDaemonClientService _daemon;
    readonly IServerLane _lane;
    readonly IServerSessionsService _sessions;
    readonly IAgentDirectory _directory;
    readonly RepoIdentityResolver _repoIdentity;
    readonly Func<string, string> _resolveMainRoot;
    readonly string? _localMachineId;
    readonly Func<Task> _reconnect;
    readonly string _cliVersion;
    readonly ITimer _batch;
    readonly Lock _lock = new();
    readonly Dictionary<string, AgentRow> _rows = new(StringComparer.Ordinal);
    readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    readonly Dictionary<string, IdeSubscription> _subscriptions = new(StringComparer.Ordinal);
    readonly IdeOutbox _outbox;
    readonly CompositeDisposable _feeds = new();
    IdeRpcLoop? _loop;
    ConnectionChanged? _connection;
    bool _initialized, _announcing, _batchArmed, _disposed;

    public IdeHost(
            IDaemonClientService daemon, IServerLane lane, IServerSessionsService sessions, IAgentDirectory directory,
            RepoIdentityResolver repoIdentity, Func<string, string> resolveMainRoot, string? localMachineId, Func<Task> reconnect,
            TimeProvider time, string cliVersion) {
        _daemon = daemon;
        _lane = lane;
        _sessions = sessions;
        _directory = directory;
        _repoIdentity = repoIdentity;
        _resolveMainRoot = resolveMainRoot;
        _localMachineId = localMachineId;
        _reconnect = reconnect;
        _cliVersion = cliVersion;
        _batch = time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _outbox = new IdeOutbox((method, parameters) => _loop!.NotifyAsync(method, parameters));
    }

    /// <summary>Registers the handlers and starts the feeds; call once, before the loop runs.</summary>
    public void Attach(IdeRpcLoop loop) {
        if (Interlocked.CompareExchange(ref _loop, loop, null) is not null) throw new InvalidOperationException("The host is already attached.");

        loop.HandleThen("initialize", Initialize);
        loop.HandleThen("sessions/subscribe", Subscribe);
        loop.Handle("sessions/unsubscribe", UnsubscribeAsync);
        loop.HandleThen("sessions/set_scope", SetScope);
        loop.HandleThen("connection/retry", Retry);

        _feeds.Add(_directory.Rows.Connect().Subscribe(OnRows));
        _feeds.Add(_daemon.Status
            .CombineLatest(_lane.Status, (daemon, lane) => ConnectionMapping.From(daemon, _daemon.DaemonName, lane))
            .Subscribe(OnConnection));
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;

            _disposed = true;
            _subscriptions.Clear();
            _changed.Clear();
            _batch.Dispose();
            _outbox.Dispose();
        }

        _feeds.Dispose();
    }

    Task<IdeReply> Initialize(JsonObject? parameters, CancellationToken ct) {
        if (parameters?["protocol_version"] is null) throw InvalidParams("protocol_version is required");

        var request = Read(parameters, IdeJsonContext.Default.InitializeParams);
        if (request.ProtocolVersion != ProtocolVersion)
            throw new IdeRpcException(IdeErrorCodes.UnsupportedProtocol, "unsupported_protocol",
                JsonSerializer.SerializeToNode(new UnsupportedProtocolData([ProtocolVersion], _cliVersion), IdeJsonContext.Default.UnsupportedProtocolData));

        _initialized = true;
        var result = JsonSerializer.SerializeToNode(
            new InitializeResult(ProtocolVersion, _cliVersion, [Capability], ScopeName()), IdeJsonContext.Default.InitializeResult);

        return Task.FromResult(new IdeReply(result, AnnounceConnectionAsync));
    }

    Task AnnounceConnectionAsync() {
        lock (_lock) {
            _announcing = true;
            if (_connection is { } connection) Send("connection/changed", connection, IdeJsonContext.Default.ConnectionChanged);
        }

        return _outbox.DrainAsync();
    }

    Task<IdeReply> Subscribe(JsonObject? parameters, CancellationToken ct) {
        RequireInitialized();
        var request = Read(parameters, IdeJsonContext.Default.SubscribeParams);
        if (string.IsNullOrEmpty(request.SubscriptionId) || request.GitRoots is null || request.GitRoots.Any(root => root is null))
            throw InvalidParams("subscription_id and git_roots are required");
        // A relative root would resolve against this process's working directory, which is not the client's.
        // A blank one selects nothing, as ProjectScope already treats it.
        if (request.GitRoots.Any(root => !string.IsNullOrWhiteSpace(root) && !Path.IsPathFullyQualified(root)))
            throw InvalidParams("git_roots must be absolute paths");

        // Resolving the roots reads git metadata, so it stays outside the lock the row feed takes.
        var subscription = new IdeSubscription(request.SubscriptionId, ProjectScope.For(request.GitRoots, _repoIdentity, _resolveMainRoot, _localMachineId));

        return Task.FromResult(new IdeReply(null, () => StartAsync(subscription)));
    }

    /// <summary>The snapshot and the registration are one step under the lock, so every row change lands either in
    /// the snapshot or in a later batch.</summary>
    Task StartAsync(IdeSubscription subscription) {
        lock (_lock) {
            if (_disposed) return Task.CompletedTask;

            List<SessionRow> rows = [];
            foreach (var row in _rows.Values) {
                if (!subscription.Scope.Matches(row)) continue;

                var wire = SessionRowMapping.From(row);
                subscription.Sent[row.Key] = wire;
                rows.Add(wire);
            }

            // Registered only once its snapshot is built: a throw above leaves no subscription without one.
            _subscriptions[subscription.Id] = subscription;
            Send("sessions/snapshot", new SessionsSnapshot(subscription.Id, [.. rows]), IdeJsonContext.Default.SessionsSnapshot, subscription);
        }

        return _outbox.DrainAsync();
    }

    async Task<JsonNode?> UnsubscribeAsync(JsonObject? parameters, CancellationToken ct) {
        RequireInitialized();
        var request = Read(parameters, IdeJsonContext.Default.UnsubscribeParams);
        if (string.IsNullOrEmpty(request.SubscriptionId)) throw InvalidParams("subscription_id is required");

        lock (_lock) _subscriptions.Remove(request.SubscriptionId);

        // Whatever of it is still queued is dropped or written before this reply.
        await _outbox.DrainAsync();

        return null;
    }

    Task<IdeReply> SetScope(JsonObject? parameters, CancellationToken ct) {
        RequireInitialized();
        var request = Read(parameters, IdeJsonContext.Default.SetScopeParams);
        _sessions.Scope = request.Scope switch {
            "mine" => RailScope.Mine,
            "team" => RailScope.Team,
            _      => throw InvalidParams("scope must be mine or team"),
        };

        return Task.FromResult(new IdeReply(null, AnnounceScopeAsync));
    }

    Task AnnounceScopeAsync() {
        // Read outside the lock: the service may publish into the directory, whose feed takes it.
        var scope = ScopeName();
        lock (_lock) Send("sessions/scope_changed", new ScopeChanged(scope), IdeJsonContext.Default.ScopeChanged);

        return _outbox.DrainAsync();
    }

    string ScopeName() => _sessions.Scope == RailScope.Team ? "team" : "mine";

    /// <summary>Announces nothing itself: the status feeds send <c>connection/changed</c> for whatever state the reconnect
    /// moves to.</summary>
    Task<IdeReply> Retry(JsonObject? parameters, CancellationToken ct) {
        RequireInitialized();

        return Task.FromResult(new IdeReply(null, _reconnect));
    }

    void OnRows(IChangeSet<AgentRow, string> changes) {
        lock (_lock) {
            if (_disposed) return;

            foreach (var change in changes) {
                if (change.Reason == ChangeReason.Remove) _rows.Remove(change.Key);
                else _rows[change.Key] = change.Current;

                if (_subscriptions.Count > 0) _changed.Add(change.Key);
            }

            if (_changed.Count == 0 || _batchArmed) return;

            _batchArmed = true;
            _batch.Change(BatchWindow, Timeout.InfiniteTimeSpan);
        }
    }

    void Flush() {
        lock (_lock) {
            _batchArmed = false;
            if (_disposed) return;

            foreach (var subscription in _subscriptions.Values) {
                List<SessionRow> upserts = [];
                List<string> removes = [];
                foreach (var key in _changed) {
                    if (_rows.TryGetValue(key, out var row) && subscription.Scope.Matches(row)) {
                        var wire = SessionRowMapping.From(row);
                        if (subscription.Sent.TryGetValue(key, out var sent) && sent == wire) continue;

                        subscription.Sent[key] = wire;
                        upserts.Add(wire);
                    } else if (subscription.Sent.Remove(key)) {
                        removes.Add(key);
                    }
                }

                if (upserts.Count > 0 || removes.Count > 0)
                    Send("sessions/changed", new SessionsChanged(subscription.Id, [.. upserts], [.. removes]), IdeJsonContext.Default.SessionsChanged, subscription);
            }

            _changed.Clear();
        }
    }

    void OnConnection(ConnectionChanged connection) {
        lock (_lock) {
            if (_disposed || connection == _connection) return;

            _connection = connection;
            if (_announcing) Send("connection/changed", connection, IdeJsonContext.Default.ConnectionChanged);
        }
    }

    /// <summary>Call under the lock, so messages queue in the order they were decided. A message for a subscription
    /// is dropped if that subscription has ended or been replaced by the time it would be written.</summary>
    void Send<T>(string method, T message, JsonTypeInfo<T> type, IdeSubscription? owner = null) =>
        _outbox.Send(method, JsonSerializer.SerializeToNode(message, type)!, owner is null ? null : () => Holds(owner));

    bool Holds(IdeSubscription subscription) {
        lock (_lock) return _subscriptions.TryGetValue(subscription.Id, out var current) && current == subscription;
    }

    void RequireInitialized() {
        if (!_initialized) throw new IdeRpcException(IdeErrorCodes.NotInitialized, "not_initialized");
    }

    static T Read<T>(JsonObject? parameters, JsonTypeInfo<T> type) where T : class {
        if (parameters is null) throw InvalidParams("params are required");

        try {
            return parameters.Deserialize(type) ?? throw InvalidParams("params are required");
        } catch (JsonException) {
            throw InvalidParams("params do not match the method");
        }
    }

    static IdeRpcException InvalidParams(string message) => new(IdeErrorCodes.InvalidParams, message);
}
