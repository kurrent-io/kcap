using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Client;

namespace Capacitor.Cli.Ide;

/// <summary>
/// Serves one session's work item and plan per subscription: a snapshot of both sections, then each section again
/// whenever a read changes it. Handlers answer at once and every read runs off the request loop; results settle under
/// the lock, which is also where every notification is queued, so they leave in the order they were decided.
/// </summary>
internal sealed class ContextHost : IDisposable {
    readonly IWorkContextSource _work;
    readonly IPlanSource _plans;
    readonly IObservable<ServerLaneStatus> _lane;
    readonly string? _serverUrl;
    readonly TimeProvider _time;
    readonly IdeOutbox _outbox;
    readonly Func<bool> _isInitialized;
    readonly Lock _lock = new();
    readonly Dictionary<string, ContextSubscription> _subscriptions = new(StringComparer.Ordinal);
    readonly LaneAccount _account = new();
    ServerLaneState? _laneState;
    IDisposable? _laneFeed;
    bool _disposed;

    /// <param name="outbox">The process's one notification queue, shared with the session host so the order holds
    /// across both.</param>
    /// <param name="isInitialized">Whether the client has completed <c>initialize</c>, which the session host answers.</param>
    public ContextHost(
            IWorkContextSource work, IPlanSource plans, IObservable<ServerLaneStatus> lane, string? serverUrl, TimeProvider time,
            IdeOutbox outbox, Func<bool> isInitialized) {
        _work = work;
        _plans = plans;
        _lane = lane;
        _serverUrl = serverUrl;
        _time = time;
        _outbox = outbox;
        _isInitialized = isInitialized;
    }

    /// <summary>Registers the handlers and follows the server connection; call once, before the loop runs.</summary>
    public void Attach(IdeRpcLoop loop) {
        loop.HandleThen("context/subscribe", Subscribe);
        loop.Handle("context/unsubscribe", UnsubscribeAsync);
        loop.Handle("context/refresh", Refresh);
        _laneFeed = _lane.Subscribe(FollowLane);
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;

            _disposed = true;
            foreach (var subscription in _subscriptions.Values) subscription.Dispose();
            _subscriptions.Clear();
        }

        _laneFeed?.Dispose();
    }

    /// <summary>Completes once every read the subscription has started has settled.</summary>
    internal Task PendingReadsForTesting(string subscriptionId) {
        lock (_lock) return _subscriptions.TryGetValue(subscriptionId, out var subscription) ? subscription.PendingReadsForTesting : Task.CompletedTask;
    }

    Task<IdeReply> Subscribe(JsonObject? parameters, CancellationToken ct) {
        RequireInitialized();
        var request = IdeHost.Read(parameters, IdeJsonContext.Default.ContextSubscribeParams);
        if (string.IsNullOrEmpty(request.SubscriptionId) || WorkContextIds.CanonicalSessionId(request.SessionId) is not { } sessionId)
            throw IdeHost.InvalidParams("subscription_id and session_id are required");

        return Task.FromResult(new IdeReply(null, () => StartAsync(request.SubscriptionId, sessionId)));
    }

    /// <summary>A subscription under the same id is replaced, and its reads still in flight settle into nothing. The
    /// snapshot is queued before any read of the new one can settle, since settling takes the lock held here.</summary>
    Task StartAsync(string id, string sessionId) {
        lock (_lock) {
            if (_disposed) return Task.CompletedTask;

            if (_subscriptions.Remove(id, out var replaced)) replaced.Dispose();
            var subscription = new ContextSubscription(id, sessionId, _work, _plans, _serverUrl, _time, Post, Publish, ForgetCredential);
            subscription.Start();
            _subscriptions[id] = subscription;
            Send("context/snapshot", subscription.TakeSnapshot(), IdeJsonContext.Default.ContextSnapshot, subscription);
        }

        return _outbox.DrainAsync();
    }

    async Task<JsonNode?> UnsubscribeAsync(JsonObject? parameters, CancellationToken ct) {
        var id = RequireSubscriptionId(parameters);
        lock (_lock) {
            if (_subscriptions.Remove(id, out var subscription)) subscription.Dispose();
        }

        // Whatever of it is still queued is dropped or written before this reply.
        await _outbox.DrainAsync();

        return null;
    }

    Task<JsonNode?> Refresh(JsonObject? parameters, CancellationToken ct) {
        var id = RequireSubscriptionId(parameters);
        lock (_lock) {
            if (!_disposed && _subscriptions.TryGetValue(id, out var subscription)) subscription.Refresh();
        }

        return Task.FromResult<JsonNode?>(null);
    }

    /// <summary>A sign-out or a connect as another account restarts every subscription from nothing; a reconnect as the same
    /// account reads again over what is shown. The first status is the one the connection already had, not a reconnect.</summary>
    void FollowLane(ServerLaneStatus status) {
        lock (_lock) {
            if (_disposed) return;

            var previous = _laneState;
            _laneState = status.State;
            var switched = _account.SwitchedBy(status);
            if (switched || status.State == ServerLaneState.SignedOut && previous != ServerLaneState.SignedOut) {
                ForgetCredential();
                foreach (var subscription in _subscriptions.Values) subscription.Restart();
            } else if (status.State == ServerLaneState.Connected && previous is not (null or ServerLaneState.Connected)) {
                foreach (var subscription in _subscriptions.Values) subscription.Refresh();
            }
        }
    }

    /// <summary>Both sources read under one credential, so neither may seed a subscription once it is gone.</summary>
    void ForgetCredential() {
        _work.InvalidateAuthentication();
        _plans.InvalidateAuthentication();
    }

    /// <summary>The serial lane both readers of every subscription settle on.</summary>
    Task Post(Action settle) {
        lock (_lock) settle();

        return Task.CompletedTask;
    }

    /// <summary>Runs under the lock, from a settling read.</summary>
    void Publish(ContextSubscription subscription) {
        if (_disposed || subscription.TakeChanges() is not { } changed) return;

        Send("context/changed", changed, IdeJsonContext.Default.ContextChanged, subscription);
    }

    /// <summary>Call under the lock. Dropped if the subscription has ended or been replaced by the time it would be written.</summary>
    void Send<T>(string method, T message, JsonTypeInfo<T> type, ContextSubscription owner) =>
        _outbox.Send(method, JsonSerializer.SerializeToNode(message, type)!, () => Holds(owner));

    bool Holds(ContextSubscription subscription) {
        lock (_lock) return _subscriptions.TryGetValue(subscription.Id, out var current) && current == subscription;
    }

    string RequireSubscriptionId(JsonObject? parameters) {
        RequireInitialized();
        var request = IdeHost.Read(parameters, IdeJsonContext.Default.ContextRefParams);
        if (string.IsNullOrEmpty(request.SubscriptionId)) throw IdeHost.InvalidParams("subscription_id is required");

        return request.SubscriptionId;
    }

    void RequireInitialized() {
        if (!_isInitialized()) throw new IdeRpcException(IdeErrorCodes.NotInitialized, "not_initialized");
    }
}
