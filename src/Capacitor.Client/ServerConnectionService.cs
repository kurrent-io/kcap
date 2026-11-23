using System.Collections.Frozen;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Net.WebSockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Models.Transcripts;
using Capacitor.Remote.Models;
using Eventuous.SignalR;
using Eventuous.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Client;

/// The app's one long-lived server connection. Handlers are registered before StartAsync so no
/// broadcast can slip past a fresh connection. A dropped connection reconnects on the ladder every
/// hub client shares and never gives up except on a 401, which parks the lane; the outer loop
/// re-dials on the same ladder only for what automatic reconnect does not cover, a cold-start
/// failure or a close the server asked for.
public sealed class ServerConnectionService : IServerLane, ILaunchClient, IAsyncDisposable {
    readonly TimeProvider _time;

    readonly string? _serverUrl;
    readonly Func<Task<string?>> _token;
    readonly HubRetryPolicy _retry;
    readonly TimeSpan[]? _reconnectDelays;
    readonly Action<ILoggingBuilder>? _hubLogging;
    readonly Action<string, (string Key, string? Value)[]>? _log;
    ServerLaneState? _loggedState;
    readonly Lock _redialLock = new();
    // True from a loop's admission until it publishes its first status, a window in which the
    // status still describes the loop it replaced.
    bool _admittedUnannounced;
    Task? _redial;
    readonly BehaviorSubject<ServerLaneStatus> _status = new(new(ServerLaneState.Dormant));
    readonly Subject<Unit> _agentsChanged = new();
    readonly Subject<Unit> _daemonsChanged = new();
    readonly Subject<Unit> _daemonAccountsChanged = new();
    readonly Subject<LaunchFailure> _launchFailures = new();
    readonly Subject<string> _permissionPending = new();
    readonly Subject<PermissionRespondedPing> _permissionResponded = new();
    readonly Subject<ServerPermissionRequest> _permissionRequests = new();
    readonly Subject<ServerElicitationRequest> _elicitations = new();
    readonly Subject<string> _sessionAccessChanged = new();
    readonly Subject<SessionPush> _sessionPushes = new();
    readonly Subject<PendingInputUpdate> _pendingInput = new();
    readonly Subject<string> _heldMessagesChanged = new();
    readonly Subject<TerminalOutputFrame> _terminalOutput = new();
    readonly Subject<TerminalSize> _terminalDimensions = new();
    // The subscription client of the live hub, replaced with it: it registers the StreamEvent
    // handler on the hub it wraps, so one per connection generation.
    volatile SignalRSubscriptionClient? _streams;
    readonly SemaphoreSlim _restartGate = new(1, 1);
    // One lock owns the whole lane lifecycle: the generation, the current loop's cancellation
    // source, and EVERY status publish. A generation names one admitted connect loop; a park, a
    // restart and teardown each advance it, so a publish carrying an older generation belongs to a
    // loop that has been superseded and is dropped rather than overwriting whatever superseded it.
    readonly Lock _lifecycleLock = new();
    int _generation;
    // The admitted loop's own source, so a park cancels the loop that was running when it decided
    // to park — never one a concurrent restart has since put in its place. Null between a restart
    // retiring the old loop and admitting the new one.
    CancellationTokenSource? _loopCts;
    // Set under the lock before the subjects are disposed, so no publish can be in flight when
    // they go.
    bool _closed;
    readonly CancellationTokenSource _lifetime = new();
    Task _loop = Task.CompletedTask;
    volatile HubConnection? _hub;

    public ServerConnectionService(
            ProfileContext? profiles, ICredentialSource? credentials, TimeProvider time, LocalRoot? localRoot = null,
            Action<string, (string Key, string? Value)[]>? log = null)
        : this(
            time,
            profiles?.Resolution.ServerUrl,
            credentials is null
                ? () => Task.FromResult<string?>(null)
                : async () => (await credentials.ResolveAsync(default)).Bearer,
            localRoot: localRoot,
            refusal: profiles is null ? null : LocalDestination.Refusal(profiles.Resolution.ServerUrl, profiles.Resolution.Endpoint),
            log: log,
            updateCheckOff: profiles?.Effective?.UpdateCheck == false) { }

    readonly LocalRoot? _localRoot;
    readonly string?    _refusal;
    readonly bool       _updateCheckOff;

    internal ServerConnectionService(
            TimeProvider time, string? serverUrl, Func<Task<string?>> accessTokenProvider,
            TimeSpan[]? reconnectDelays = null, Action<ILoggingBuilder>? hubLogging = null,
            LocalRoot? localRoot = null, string? refusal = null,
            Action<string, (string Key, string? Value)[]>? log = null,
            bool updateCheckOff = false) {
        _localRoot      = localRoot;
        _log            = log;
        _refusal        = refusal;
        _updateCheckOff = updateCheckOff;
        _time = time;
        _serverUrl = string.IsNullOrEmpty(serverUrl) ? null : serverUrl.TrimEnd('/');
        _token = accessTokenProvider;
        _reconnectDelays = reconnectDelays;
        _retry = new HubRetryPolicy(ladder: reconnectDelays);
        _hubLogging = hubLogging;
    }

    /// SignalR awaits the token on every negotiate and reconnect with no deadline of its own, so a
    /// resolve that never returns would hold the lane down for the life of the process. Above the
    /// token store's own worst case: its cross-process lock wait plus a full refresh budget.
    internal TimeSpan TokenTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// How long a restart waits for the loop it retires. A loop still running past it is abandoned
    /// rather than awaited, so one stuck in a hub's start or dispose cannot keep the lane down.
    internal TimeSpan LoopRetireGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// How long a launch that found no live hub waits on the redial it starts.
    internal TimeSpan LaunchRedialWait { get; init; } = TimeSpan.FromSeconds(10);

    Task<string?> TokenAsync() => _token().WaitAsync(TokenTimeout, _time);

    public IObservable<ServerLaneStatus> Status => _status.AsObservable();
    public IObservable<Unit> AgentInstancesChanged => _agentsChanged.AsObservable();
    public IObservable<Unit> DaemonsChanged => _daemonsChanged.AsObservable();
    public IObservable<Unit> DaemonAccountsChanged => _daemonAccountsChanged.AsObservable();
    public IObservable<LaunchFailure> LaunchFailures => _launchFailures.AsObservable();
    public IObservable<string> PermissionPending => _permissionPending.AsObservable();
    public IObservable<PermissionRespondedPing> PermissionResponded => _permissionResponded.AsObservable();
    public IObservable<ServerPermissionRequest> PermissionRequests => _permissionRequests.AsObservable();
    public IObservable<ServerElicitationRequest> ElicitationRequests => _elicitations.AsObservable();
    public IObservable<string> SessionAccessChanged => _sessionAccessChanged.AsObservable();
    public IObservable<SessionPush> SessionPushes => _sessionPushes.AsObservable();
    public IObservable<PendingInputUpdate> PendingInputChanged => _pendingInput.AsObservable();
    public IObservable<string> HeldMessagesChanged => _heldMessagesChanged.AsObservable();
    public IObservable<TerminalOutputFrame> TerminalOutput => _terminalOutput.AsObservable();
    public IObservable<TerminalSize> TerminalDimensions => _terminalDimensions.AsObservable();

    public void Start() {
        if (_serverUrl is null) return;
        _ = RestartAsync();
    }

    /// Unconditional park, same semantics as the 401-negotiate path. RestartAsync revives it.
    internal void ParkSignedOut() => ParkSignedOutCore(ifGeneration: null, detail: null);

    /// Parks only while `ifEpoch` is still the lane's current generation — the generation of the
    /// Connected status the caller's own decision was made under. RemoteAgentsService releases its
    /// cache lock before invoking onUnauthorized (taking it around the callback would invert it
    /// against this lane's lock), so by the time this runs a park, a restart or a teardown may have
    /// moved the lane on; re-validating here, atomically with the park itself, is what that
    /// callback could not do for itself.
    public void ParkSignedOut(int ifEpoch) => ParkSignedOutCore(ifEpoch, detail: null);

    void ParkSignedOutCore(int? ifGeneration, string? detail) {
        int parked;
        bool logged;
        lock (_lifecycleLock) {
            if (_closed) return;
            if (ifGeneration is { } generation && generation != _generation) return;
            // Advancing first is what makes the park terminal: every publish the parked loop still
            // has in flight — its Connecting, its Retrying, a Connected waiting on ReadSubjectAsync —
            // now carries a stale generation and is dropped. Cancelling the captured source under
            // the same lock stops the loop admitted at THIS generation and no other.
            _generation++;
            _admittedUnannounced = false;
            _status.OnNext(new(ServerLaneState.SignedOut, detail, Epoch: _generation));
            _loopCts?.Cancel();
            parked = _generation;
            logged = ShouldLog(ServerLaneState.SignedOut);
        }
        if (logged) LogTransition(new(ServerLaneState.SignedOut, detail), parked);
    }

    /// Retires the running loop and admits a new one under a fresh generation. A park landing while
    /// the old loop is still being awaited wins: the generation this restart reserved is no longer
    /// current, so no loop is admitted and the lane stays parked until the next restart.
    public Task RestartAsync(CancellationToken ct = default) => RestartAsync(onlyIf: null, ct);

    /// With `onlyIf`, restarts only when it holds, read under the restart gate and the lifecycle lock:
    /// after any restart already under way has admitted its loop, and atomically with a park.
    async Task RestartAsync(Func<bool>? onlyIf, CancellationToken ct) {
        if (_serverUrl is null || _lifetime.IsCancellationRequested) return;
        await _restartGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_lifetime.IsCancellationRequested) return;
            CancellationTokenSource? previous;
            int generation;
            lock (_lifecycleLock) {
                if (_closed) return;
                if (onlyIf is not null && !onlyIf()) return;
                previous = _loopCts;
                // Unreachable to a park from here, so disposing it below cannot race one.
                _loopCts = null;
                _generation++;
                generation = _generation;
            }
            previous?.Cancel();
            if (await RetiredAsync(_loop).ConfigureAwait(false)) previous?.Dispose();
            else Log("server_lane_loop_abandoned", ("epoch", generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            CancellationToken token;
            lock (_lifecycleLock) {
                if (_closed || generation != _generation) return;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _loopCts = cts;
                token = cts.Token;
                _admittedUnannounced = true;
            }
            _loop = Task.Run(() => RunAsync(generation, token), CancellationToken.None);
        } finally {
            _restartGate.Release();
        }
    }

    async Task RunAsync(int generation, CancellationToken ct) {
        var attempt = 0;
        while (!ct.IsCancellationRequested) {
            // A refused publish means this loop has been superseded — parked, restarted or torn
            // down — and has nothing left to dial for.
            if (!Publish(generation, new(ServerLaneState.Connecting))) return;
            HubConnection? hub = null;
            SignalRSubscriptionClient? streams = null;
            try {
                var refusal = new StrongBox<Exception?>();
                hub = Build(generation, refusal);
                var capturedHub = hub;
                streams = new SignalRSubscriptionClient(hub);

                // Registered before StartAsync (SignalR supports that), so a close during the
                // ReadSubjectAsync token read below — or during StartAsync itself — is still
                // observed, never lost with status stuck on a dead hub.
                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var spans  = new ConnectedSpans(_time);
                hub.Closed += ex => { spans.Down(); closed.TrySetResult(ex); return Task.CompletedTask;  };
                hub.Reconnecting += _ => {
                    spans.Down();
                    Publish(generation, new(ServerLaneState.Retrying, "reconnecting"));
                    return Task.CompletedTask;
                };
                hub.Reconnected += async _ => {
                    spans.Up();
                    EndTails(capturedHub);
                    var subject = await ReadSubjectAsync().ConfigureAwait(false);
                    PublishConnected(generation, capturedHub, subject);
                };

                await hub.StartAsync(ct).ConfigureAwait(false);
                spans.Up();
                lock (_lifecycleLock) {
                    // A loop a restart abandoned can finish starting late; it must not replace the current loop's hub.
                    if (_closed || generation != _generation) throw new OperationCanceledException(ct);
                    _hub = hub;
                    _streams = streams;
                }
                var connectedSubject = await ReadSubjectAsync().ConfigureAwait(false);
                PublishConnected(generation, capturedHub, connectedSubject);

                Exception? closeReason;
                await using (ct.Register(() => closed.TrySetResult(null)))
                    closeReason = await closed.Task.ConfigureAwait(false);

                // Only a connection that stayed up longer than the ladder's longest step starts it over: one the server
                // takes and closes at once keeps climbing, rather than being redialled every second.
                spans.Down();
                if (spans.Longest >= _retry.Delay(long.MaxValue)) attempt = 0;

                if (Volatile.Read(ref refusal.Value) is { } refused) {
                    ParkSignedOutCore(generation, refused.Message);
                    return;
                }

                if (!ct.IsCancellationRequested)
                    Publish(generation, new(ServerLaneState.Retrying, closeReason?.Message, Failure: LaneFailures.Unanswered(closeReason)));
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) when (IsUnauthorized(ex)) {
                // The credential is what kcap login repairs, not this loop — retrying a 401
                // negotiate forever would just burn the backoff ladder for no reason. The lane
                // parks here until RestartAsync (wired to sign-in completion) admits a fresh loop,
                // through the same park a delayed unauthorized fetch takes.
                ParkSignedOutCore(generation, ex.Message);
                return;
            } catch (Exception ex) {
                Publish(generation, new(ServerLaneState.Retrying, ex.Message, Failure: LaneFailures.Unanswered(ex)));
            } finally {
                // An abandoned loop finishing late must not clear the connection a newer loop owns.
                lock (_lifecycleLock) {
                    if (ReferenceEquals(_hub, hub)) {
                        _hub = null;
                        _streams = null;
                    }
                }
                if (hub is not null) await hub.DisposeAsync().ConfigureAwait(false);
                // The hub goes first: disposed, the client's courtesy unsubscribe finds a dead
                // connection and skips it (the library itself catches ObjectDisposedException),
                // so it never makes a server round-trip on the shutdown path. A failure here still
                // must not turn a loop exit into a fault.
                if (streams is not null) {
                    try { await streams.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
                }
            }

            if (ct.IsCancellationRequested) break;
            var delay = _retry.Delay(attempt++);
            try { await Task.Delay(delay, _time, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    /// The one place a lifecycle status reaches subscribers: it carries the generation that
    /// produced it, and lands only while that generation is still current. False means the caller
    /// has been superseded.
    bool Publish(int generation, ServerLaneStatus status) {
        bool logged;
        lock (_lifecycleLock) {
            if (_closed || generation != _generation) return false;
            _admittedUnannounced = false;
            _status.OnNext(status with { Epoch = generation });
            logged = ShouldLog(status.State);
        }
        if (logged) LogTransition(status, generation);
        return true;
    }

    /// One line per change of state, skipping Connecting: a lane retrying on the ladder alternates
    /// Connecting and Retrying every step, and only its first failure says anything new. Called under
    /// the lifecycle lock, so the state it tracks follows the order statuses were published in.
    bool ShouldLog(ServerLaneState state) {
        if (_log is null || state == ServerLaneState.Connecting || _loggedState == state) return false;
        _loggedState = state;
        return true;
    }

    void LogTransition(ServerLaneStatus status, int generation) =>
        Log("server_lane", ("state", status.State.ToString()), ("detail", status.Detail),
            ("epoch", generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    void Log(string evt, params (string Key, string? Value)[] fields) => _log?.Invoke(evt, fields);

    async Task<bool> RetiredAsync(Task loop) {
        try {
            await AwaitQuietly(loop).WaitAsync(LoopRetireGrace, _time).ConfigureAwait(false);
            return true;
        } catch (TimeoutException) {
            return false;
        }
    }

    /// <summary>What SignalR's own WebSocket transport sends, after the destination check its handler factory cannot
    /// see.</summary>
    async ValueTask<WebSocket> ConnectWebSocketAsync(WebSocketConnectionContext context, CancellationToken ct) {
        LocalDestination.Check(context.Uri, _localRoot, _serverUrl, _refusal);

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Requested-With", "XMLHttpRequest");

        if (context.Options.AccessTokenProvider is { } provider && await provider() is { Length: > 0 } token)
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        using var cap    = new CancellationTokenSource(WebSocketConnectTimeout, _time);
        using var capped = CancellationTokenSource.CreateLinkedTokenSource(ct, cap.Token);

        try {
            await socket.ConnectAsync(context.Uri, capped.Token);
            return socket;
        } catch (OperationCanceledException) when (cap.IsCancellationRequested && !ct.IsCancellationRequested) {
            socket.Dispose();
            throw new TimeoutException($"No answer from {context.Uri.Host} within {WebSocketConnectTimeout.TotalSeconds:F0}s while opening the WebSocket.");
        } catch {
            socket.Dispose();
            throw;
        }
    }

    static HttpMessageHandler NoRedirects(HttpMessageHandler handler) {
        switch (handler) {
            case HttpClientHandler h:  h.AllowAutoRedirect = false; break;
            case SocketsHttpHandler s: s.AllowAutoRedirect = false; break;
        }

        return handler;
    }

    /// SignalR closes a connection the policy gave up on with an exception of its own, so the 401 is
    /// kept in `refusal`. One per connection: loops can overlap once a restart abandons one, and a
    /// refusal must only park the loop whose hub met it. For the same reason a push reaches subscribers
    /// only while `generation` is current: a retired hub still connected must not deliver another
    /// account's permission requests or sessions.
    HubConnection Build(int generation, StrongBox<Exception?> refusal) {
        var builder = new HubConnectionBuilder()
            .WithUrl($"{_serverUrl}/hubs/sessions", o => {
                o.AccessTokenProvider = TokenAsync;
                HttpClientExtensions.AddObservationHeaders(o.Headers, _updateCheckOff);
                // Every negotiation and every WebSocket upgrade, reconnects included.
                // The guard follows redirects itself, so it can check each hop; the handler under it follows none.
                o.HttpMessageHandlerFactory = inner => new LocalDestinationHandler(_localRoot, _serverUrl, _refusal, followRedirects: true) {
                    InnerHandler = NoRedirects(inner),
                };
                o.WebSocketFactory = ConnectWebSocketAsync;
            });
        if (_hubLogging is { } logging) builder.ConfigureLogging(logging);
        var retry = new HubRetryPolicy(ex => {
            if (!IsUnauthorized(ex)) return false;

            Volatile.Write(ref refusal.Value, ex);
            return true;
        }, _reconnectDelays);
        var hub = builder.WithAutomaticReconnect(retry)
            .AddJsonProtocol(o => {
                // Any resolver in the chain replaces SignalR's implicit reflection fallback, so where reflection is
                // on, a hub type the generated contexts do not name would throw.
                if (JsonSerializer.IsReflectionEnabledByDefault) LaunchHubJson.Configure(o.PayloadSerializerOptions);
                else ConfigureHubPayload(o.PayloadSerializerOptions);
            })
            .Build();
        hub.KeepAliveInterval = HubRetryPolicy.KeepAliveInterval;
        hub.On(HubBroadcasts.AgentInstancesChanged, () => Push(generation, _agentsChanged, Unit.Default));
        hub.On(HubBroadcasts.DaemonsChanged, () => Push(generation, _daemonsChanged, Unit.Default));
        hub.On(HubBroadcasts.DaemonAccountsChanged, () => Push(generation, _daemonAccountsChanged, Unit.Default));
        hub.On<string, string>(HubBroadcasts.LaunchFailed, (agentId, reason) => Push(generation, _launchFailures, new(agentId, reason)));
        hub.On<string>(HubBroadcasts.PermissionPending, sid => Push(generation, _permissionPending, sid));
        hub.On<string, string?>(HubBroadcasts.PermissionResponded, (sid, rid) => Push(generation, _permissionResponded, new(sid, rid)));
        hub.On<string, string, string?, JsonElement?, JsonElement?>(HubBroadcasts.PermissionRequested, (sid, rid, tool, input, options) => {
            Push(generation, _permissionRequests, ServerPermissionRequest.From(sid, rid, tool, input, options));
        });
        // JsonElement, not the typed array: binding the options to a record with required members
        // makes SignalR drop the WHOLE push over one malformed option, so it is parsed leniently
        // instead — an array that does not read as options is no options, which asks for free text.
        hub.On<string, string, string, JsonElement?, bool>(HubBroadcasts.AcpElicitationRequested, (sid, rid, prompt, options, multi) => {
            Push(generation, _elicitations, new ServerElicitationRequest(sid, rid, prompt, ServerPermissionRequest.ParseOptions(options) ?? [], multi));
        });
        hub.On<string>(HubBroadcasts.SessionAccessChanged, sid => Push(generation, _sessionAccessChanged, sid));
        hub.On<string>(HubBroadcasts.ActiveSessionAdded, id => Push(generation, _sessionPushes, new(SessionPushKind.ActiveAdded, id)));
        hub.On<string>(HubBroadcasts.ActiveSessionChanged, id => Push(generation, _sessionPushes, new(SessionPushKind.ActiveChanged, id)));
        hub.On<string>(HubBroadcasts.ActiveSessionRemoved, id => Push(generation, _sessionPushes, new(SessionPushKind.ActiveRemoved, id)));
        hub.On<string>(HubBroadcasts.SessionTitleChanged, id => Push(generation, _sessionPushes, new(SessionPushKind.TitleChanged, id)));
        hub.On<string>(HubBroadcasts.SessionDeleted, id => Push(generation, _sessionPushes, new(SessionPushKind.Deleted, id)));
        hub.On<string, string, JsonElement?>(HubBroadcasts.PendingInputChanged, (_, sessionId, items) => {
            if (ParseQueue(items) is { } queue) Push(generation, _pendingInput, new PendingInputUpdate(sessionId, queue));
        });
        hub.On<string>(HubBroadcasts.HeldMessagesChanged, id => Push(generation, _heldMessagesChanged, id));
        hub.On<string, string>(HubBroadcasts.TerminalOutput, (agentId, base64) => Push(generation, _terminalOutput, new(agentId, base64)));
        hub.On<string, int, int>(HubBroadcasts.TerminalDimensions, (agentId, cols, rows) => {
            Push(generation, _terminalDimensions, new TerminalSize(agentId, cols, rows));
        });
        foreach (var name in UnusedNudges) hub.On(name, static () => { });
        foreach (var name in UnusedPings) hub.On<JsonElement>(name, static _ => { });
        return hub;
    }

    /// The hub payload options of a process with reflection-based JSON off: every type comes from a generated context.
    internal static void ConfigureHubPayload(JsonSerializerOptions options) {
        LaunchHubJson.Configure(options);
        options.TypeInfoResolverChain.Insert(0, RemoteModelsJsonContext.Default);
        options.TypeInfoResolverChain.Insert(1, ClientHubJsonContext.Default);
    }

    /// Checked and delivered under the lock that advances the generation, so a push from a retired
    /// hub cannot pass the check and then land after a restart has connected another account.
    void Push<T>(int generation, Subject<T> subject, T value) {
        lock (_lifecycleLock) {
            if (_closed || generation != _generation) return;
            subject.OnNext(value);
        }
    }

    /// Pushes the app has no use for, by arity. Each still gets a handler: SignalR binds a push with
    /// none against no parameters, so one carrying an argument costs a thrown and caught exception.
    /// The argument binds as a JsonElement, so only the count has to match the server's.
    static readonly string[] UnusedNudges = [
        HubBroadcasts.FlowsChanged, HubBroadcasts.WorkItemsChanged, HubBroadcasts.ProjectsChanged, HubBroadcasts.WelcomeStateChanged,
    ];
    static readonly string[] UnusedPings = [
        HubBroadcasts.SessionEvalCompleted, HubBroadcasts.SessionWhatsDoneGenerated, HubBroadcasts.SubagentAdopted,
        HubBroadcasts.RawStreamAccessRevoked,
    ];

    /// Connected additionally requires `hub` to still be the live connection and still be connected
    /// at publish time: ReadSubjectAsync's await can outlast either, and a Connected published over a
    /// dead hub reaches downstream DistinctUntilChanged and swallows the real transition that
    /// followed it. Folded into the same locked check as the generation, so neither the Reconnecting
    /// handler nor a park can interleave with it.
    void PublishConnected(int generation, HubConnection hub, string? subject) {
        lock (_lifecycleLock) {
            if (!ReferenceEquals(_hub, hub) || hub.State != HubConnectionState.Connected) return;
            Publish(generation, new(ServerLaneState.Connected, Subject: subject));
        }
    }

    async Task<string?> ReadSubjectAsync() {
        try {
            return await TokenAsync().ConfigureAwait(false) is { } token ? JwtClaims.TryGetString(token, "sub") : null;
        } catch {
            return null;
        }
    }

    public async Task<IReadOnlyList<DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return null;
        try {
            return await hub.InvokeAsync<List<DaemonInfo>>(HubMethods.GetConnectedDaemons, ct).ConfigureAwait(false);
        } catch (Exception) {
            return null;
        }
    }

    public async Task<IReadOnlyList<DaemonAccountsDto>?> GetDaemonAccountsAsync(CancellationToken ct) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return null;
        try {
            return await hub.InvokeAsync<List<DaemonAccountsDto>>(HubMethods.GetDaemonAccounts, ct).ConfigureAwait(false);
        } catch (Exception) {
            return null;
        }
    }

    /// A hung call would leave the hand-off composer checking for as long as it hangs.
    static readonly TimeSpan LaunchFeaturesTimeout = TimeSpan.FromSeconds(10);

    /// Nothing in SignalR or the socket bounds the TLS handshake and upgrade reply: a network that drops
    /// after the SYN otherwise holds the lane in Connecting until the kernel gives up, about 15 minutes.
    static readonly TimeSpan WebSocketConnectTimeout = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return null;
        using var timeout = new CancellationTokenSource(LaunchFeaturesTimeout, _time);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try {
            var features = await hub.InvokeAsync<string[]?>(HubMethods.GetLaunchFeatures, call.Token).ConfigureAwait(false);
            return features is { Length: > 0 } ? features.ToFrozenSet(StringComparer.Ordinal) : FrozenSet<string>.Empty;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (HubException ex) when (IsMissingMethod(ex, HubMethods.GetLaunchFeatures)) {
            return FrozenSet<string>.Empty;
        } catch (Exception) {
            return null;
        }
    }

    /// An older server's answer to a method it lacks, in either form a server gives it: the binding
    /// failure of a JSON-protocol server, or a dispatcher's unknown method. Any other failure is no answer.
    internal static bool IsMissingMethod(HubException ex, string method) =>
        string.Equals(ex.Message, $"Failed to invoke '{method}' due to an error on the server. HubException: Method does not exist.", StringComparison.Ordinal)
     || ex.Message.StartsWith($"Unknown hub method '{method}'", StringComparison.Ordinal);

    public Task<HubCallOutcome> RequestStopAgentAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.RequestStopAgent, ct, agentId);
    public Task<HubCallOutcome> UnsubscribeFromChatAsync(string sessionId, CancellationToken ct) => InvokeAsync(HubMethods.UnsubscribeFromChat, ct, sessionId);
    public Task<HubCallOutcome> RegisterSessionAccessWatchAsync(string sessionId, CancellationToken ct) => InvokeAsync(HubMethods.RegisterSessionAccessWatch, ct, sessionId);

    /// One tail per stream at a time. The subscription client keys its registrations by stream
    /// name and removes the key unconditionally when an enumeration ends, so a replacement that
    /// registered before the previous enumeration finished would lose its registration to that
    /// cleanup and receive nothing: a new tail ends the previous one and waits for its cleanup
    /// first, and the replaced consumer sees an ended tail. Ending an enumeration also removes
    /// only the client-side registration — the server keeps pushing the stream to this
    /// connection until told otherwise — so a tail that ends with the hub up unsubscribes there.
    public async IAsyncEnumerable<StreamEventEnvelope> TailStreamAsync(
            string stream, ulong? fromPosition, [EnumeratorCancellation] CancellationToken ct) {
        var slot = new TailSlot(ct);
        TailSlot? previous;
        lock (_tailLock) {
            _tails.TryGetValue(stream, out previous);
            _tails[stream] = slot;
        }
        try {
            if (previous is not null) {
                previous.Cts.Cancel();
                await previous.Done.Task.ConfigureAwait(false);
            }
            SignalRSubscriptionClient? streams;
            HubConnection? hub;
            // As a pair, under the lock that installs and clears them, so both are from one connection.
            lock (_lifecycleLock) {
                streams = _streams;
                hub = _hub;
            }
            if (streams is null || hub is not { State: HubConnectionState.Connected }) yield break;
            lock (_tailLock) slot.Hub = hub;
            var source = streams.SubscribeAsync(stream, fromPosition, slot.Cts.Token).GetAsyncEnumerator(slot.Cts.Token);
            var refused = false;
            try {
                while (true) {
                    bool more;
                    try {
                        more = await source.MoveNextAsync().ConfigureAwait(false);
                    } catch (HubException) {
                        refused = true;
                        throw;
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        throw;
                    } catch (Exception) {
                        // Lane loss is data, not an error: the next Connected re-tails from the last
                        // position, so the consumer sees an ended tail, never a transport fault. This
                        // also catches the library's own reconnect-exhausted close, which completes the
                        // channel with an OperationCanceledException carrying ITS OWN internal token —
                        // indistinguishable from ours by type, so only our own `ct` firing re-throws.
                        more = false;
                    }
                    if (!more) yield break;
                    yield return source.Current;
                }
            } finally {
                await source.DisposeAsync().ConfigureAwait(false);
                if (!refused && hub.State == HubConnectionState.Connected) {
                    try { await hub.InvokeAsync(SignalRSubscriptionMethods.Unsubscribe, stream, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { }
                }
            }
        } finally {
            lock (_tailLock) { if (ReferenceEquals(_tails.GetValueOrDefault(stream), slot)) _tails.Remove(stream); }
            slot.Cts.Dispose();
            slot.Done.TrySetResult();
        }
    }

    /// The subscription client resubscribes a reconnected connection's streams one at a time and stops at the first
    /// the server refuses, leaving the rest open and silent. Ending every tail instead has each consumer re-tail from
    /// its own position, so a refusal reaches only the stream it was for.
    /// Only the tails subscribed on `hub`: a retired hub coming back must leave its replacement's alone.
    void EndTails(HubConnection hub) {
        lock (_tailLock) {
            foreach (var slot in _tails.Values)
                if (ReferenceEquals(slot.Hub, hub)) slot.Cts.Cancel();
        }
    }

    readonly Dictionary<string, TailSlot> _tails = new(StringComparer.Ordinal);
    readonly Lock _tailLock = new();

    sealed class TailSlot(CancellationToken ct) {
        public readonly CancellationTokenSource Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HubConnection? Hub;
    }

    public Task<HubCallOutcome> SubscribeToTerminalAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.SubscribeToTerminal, ct, agentId);
    public Task<HubCallOutcome> UnsubscribeFromTerminalAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.UnsubscribeFromTerminal, ct, agentId);
    public Task<HubCallOutcome> RequestResizeTerminalAsync(string agentId, int cols, int rows, CancellationToken ct) => InvokeAsync(HubMethods.RequestResizeTerminal, ct, agentId, cols, rows);
    public Task<HubCallOutcome> ReleaseResizeTerminalAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.ReleaseResizeTerminal, ct, agentId);
    // Three arguments always: the method's arity is frozen and SignalR does not backfill a
    // missing trailing one.
    public Task<HubCallOutcome> SendUserInputAsync(string agentId, string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) =>
        InvokeAsync(HubMethods.SendUserInput, ct, agentId, text, attachmentIds.Count > 0 ? attachmentIds.ToArray() : null);
    public Task<HubCallOutcome> DiscardQueuedInputAsync(string agentId, Guid dispatchId, CancellationToken ct) =>
        InvokeAsync(HubMethods.DiscardQueuedInput, ct, new DiscardQueuedInputRequest(agentId, dispatchId));
    public Task<HubCallOutcome> SendSpecialKeyAsync(string agentId, string key, CancellationToken ct) => InvokeAsync(HubMethods.SendSpecialKey, ct, agentId, key);
    public Task<HubCallOutcome> SendTerminalInputAsync(string agentId, byte[] data, CancellationToken ct) =>
        InvokeAsync(HubMethods.SendTerminalInput, ct, new SendTerminalInputRequest(agentId, Convert.ToBase64String(data)));

    /// Denied is the server's session-visibility refusal (HubException carrying WireTokens.
    /// SessionNotVisible); every other exception is Failed with its message, and a lane with no
    /// live hub answers NotConnected without dialing.
    async Task<HubCallOutcome> InvokeAsync(string method, CancellationToken ct, params object?[] args) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return HubCallOutcome.NotConnected;
        try {
            await hub.InvokeCoreAsync(method, args, ct).ConfigureAwait(false);
            return HubCallOutcome.Ok;
        } catch (OperationCanceledException) {
            throw;
        } catch (HubException ex) when (ex.Message.Contains(WireTokens.SessionNotVisible, StringComparison.Ordinal)) {
            return HubCallOutcome.Denied(ex.Message);
        } catch (Exception ex) {
            return HubCallOutcome.Failed(ex.Message);
        }
    }

    /// The join answers with the session's queue; it rides the same stream as the pushes so a
    /// consumer sees one shape.
    public async Task<HubCallOutcome> SubscribeToChatAsync(string sessionId, CancellationToken ct) {
        var (outcome, snapshot) = await InvokeAsync<JsonElement?>(HubMethods.SubscribeToChat, ct, sessionId).ConfigureAwait(false);
        if (outcome.Result == HubCallResult.Ok && ParseQueue(snapshot) is { } queue) _pendingInput.OnNext(new(sessionId, queue));
        return outcome;
    }

    /// Null for a payload that is not a readable queue — never an empty one, which a consumer
    /// would read as the server having dropped every prompt it holds.
    static IReadOnlyList<QueuedInputItem>? ParseQueue(JsonElement? items) {
        if (items is not { IsArray: true } array) return null;
        try { return array.Deserialize(RemoteModelsJsonContext.Default.QueuedInputItemArray); }
        catch (JsonException) { return null; }
    }

    async Task<(HubCallOutcome Outcome, T? Result)> InvokeAsync<T>(string method, CancellationToken ct, params object?[] args) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return (HubCallOutcome.NotConnected, default);
        try {
            return (HubCallOutcome.Ok, await hub.InvokeCoreAsync<T>(method, args, ct).ConfigureAwait(false));
        } catch (OperationCanceledException) {
            throw;
        } catch (HubException ex) when (ex.Message.Contains(WireTokens.SessionNotVisible, StringComparison.Ordinal)) {
            return (HubCallOutcome.Denied(ex.Message), default);
        } catch (Exception ex) {
            return (HubCallOutcome.Failed(ex.Message), default);
        }
    }

    public async Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
        if (_status.Value.State == ServerLaneState.SignedOut)
            return new LaunchOutcome(false, null, "Not signed in to the server.", Unauthorized: true);
        try {
            var hub = _hub is { State: HubConnectionState.Connected } live ? live : await RedialAsync(ct).ConfigureAwait(false);
            if (hub is null)
                return _status.Value.State == ServerLaneState.SignedOut
                    ? new LaunchOutcome(false, null, "Not signed in to the server.", Unauthorized: true)
                    : new LaunchOutcome(false, null, "Not connected to the server.");
            var agentId = await hub.InvokeAsync<string>(
                HubMethods.RequestLaunchAgentV2, LaunchPayload.For(request), ct).ConfigureAwait(false);
            return new LaunchOutcome(Started: true, AgentId: agentId, Error: null);
        } catch (Exception ex) {
            return new LaunchOutcome(false, null, ex.Message, IsUnauthorized(ex));
        }
    }

    /// A launch is the user asking for the server now, so it redials at once instead of leaving the
    /// lane to its ladder, then waits a short while for the result. Concurrent launches share one
    /// redial until it settles: a second restart would cancel the connection the first is waiting on.
    async Task<HubConnection?> RedialAsync(CancellationToken ct) {
        if (_serverUrl is null) return null;
        Task redial;
        lock (_redialLock) {
            redial = _redial is { IsCompleted: false } running ? running : _redial = RedialOnceAsync();
        }
        await redial.WaitAsync(ct).ConfigureAwait(false);
        return _hub is { State: HubConnectionState.Connected } hub ? hub : null;
    }

    async Task RedialOnceAsync() {
        Log("server_lane_redial", ("trigger", "launch"), ("state", _status.Value.State.ToString()));
        // One deadline across the restart and the wait for its connection; a restart still retiring
        // a stuck loop past it carries on without the launch.
        var started = _time.GetTimestamp();
        try {
            await RestartAsync(NeedsRedial, CancellationToken.None).WaitAsync(LaunchRedialWait, _time).ConfigureAwait(false);
            var left = LaunchRedialWait - _time.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) return;
            // Subscribed after the restart, so a Connected replayed from the hub it retired cannot answer for its successor.
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = _status.Subscribe(s => {
                if (s.State == ServerLaneState.SignedOut || _hub is { State: HubConnectionState.Connected }) ready.TrySetResult();
            });
            await ready.Task.WaitAsync(left, _time).ConfigureAwait(false);
        } catch (TimeoutException) { }
    }

    /// Only a lane waiting out its ladder is redialled: a Connecting one has an attempt in flight
    /// (a sign-in restart's, say) that a restart would cancel, and a parked one waits for sign-in.
    bool NeedsRedial() =>
        !_admittedUnannounced
        && _hub is not { State: HubConnectionState.Connected }
        && _status.Value.State is ServerLaneState.Retrying or ServerLaneState.Dormant;

    /// Walks the chain because SignalR surfaces the negotiate failure wrapped as often as bare.
    internal static bool IsUnauthorized(Exception ex) {
        for (Exception? e = ex; e is not null; e = e.InnerException) {
            if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }) return true;
        }
        return false;
    }

    static async Task AwaitQuietly(Task t) {
        try { await t.ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync() {
        _lifetime.Cancel();
        await _restartGate.WaitAsync().ConfigureAwait(false);
        try {
            CancellationTokenSource? loop;
            lock (_lifecycleLock) {
                _closed = true;
                _generation++;
                loop = _loopCts;
                _loopCts = null;
            }
            loop?.Cancel();
            await AwaitQuietly(_loop).ConfigureAwait(false);
            loop?.Dispose();
        } finally {
            _restartGate.Release();
        }
        _status.Dispose();
        _agentsChanged.Dispose();
        _daemonsChanged.Dispose();
        _daemonAccountsChanged.Dispose();
        _launchFailures.Dispose();
        _permissionPending.Dispose();
        _permissionResponded.Dispose();
        _permissionRequests.Dispose();
        _elicitations.Dispose();
        _sessionAccessChanged.Dispose();
        _sessionPushes.Dispose();
        _pendingInput.Dispose();
        _heldMessagesChanged.Dispose();
        _terminalOutput.Dispose();
        _terminalDimensions.Dispose();
    }
}
