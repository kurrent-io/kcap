using System.Net.Sockets;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The daemon's steering socket: HTTP/1.1 on a Unix-domain socket, one request per connection, routed by path to POST
/// handlers. It carries no token, because anything that can open the socket is the owner, and a credential must never
/// reach the environment a model's shell can read. Everything a client controls is bounded: header and body size, the
/// time to finish a request, and how many connections are open at once.
/// </summary>
internal sealed partial class SteeringEndpoint(
        string socketPath,
        IReadOnlyDictionary<string, Func<SteeringHttpRequest, CancellationToken, Task<SteeringHttpResponse>>> postRoutes,
        TimeProvider time,
        ILogger logger
    ) : IAsyncDisposable {
    static readonly TimeSpan AcceptRetry = TimeSpan.FromMilliseconds(100);

    public int MaxHeaderBytes { get; init; } = 8 * 1024;

    public int MaxBodyBytes { get; init; } = 64 * 1024;

    /// <summary>From accept to the last byte of the request, and again for writing the answer.</summary>
    public TimeSpan RequestDeadline { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxConnections { get; init; } = 16;

    readonly Lock _gate = new();
    (Socket Listener, CancellationTokenSource Stop, Task Accepting, HashSet<Task> Connections)? _bound;

    public bool IsBound { get { lock (_gate) return _bound is not null; } }

    /// <summary>Binds the socket owner-only, in a directory made owner-only first, and serves it. Bound already is
    /// bound.</summary>
    public bool TryBind(out string? failure) {
        failure = null;

        lock (_gate) {
            if (_bound is not null) return true;

            if (OperatingSystem.IsWindows()) {
                failure = "steering isn't offered on Windows";

                return false;
            }

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            try {
                var directory = Path.GetDirectoryName(socketPath)!;
                OwnerOnlyDirectory.Ensure(directory);
                OwnerOnlyDirectory.CreateVerified(directory);

                // Left by a daemon of this name that died bound; the name's lock rules out a live one.
                if (File.Exists(socketPath)) File.Delete(socketPath);

                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                listener.Listen(MaxConnections);
            } catch (Exception ex) {
                listener.Dispose();

                failure = ex.Message;
                LogBindFailed(socketPath, ex.Message);

                return false;
            }

            var stop        = new CancellationTokenSource();
            var connections = new HashSet<Task>();
            var slots       = new SemaphoreSlim(MaxConnections, MaxConnections);
            var accepting   = Task.Run(() => AcceptAsync(listener, slots, connections, stop.Token));

            _bound = (listener, stop, accepting, connections);
            LogBound(socketPath);

            return true;
        }
    }

    /// <summary>Closes the socket and removes its file, then waits for the connections it was serving, which are
    /// cut off.</summary>
    public async Task UnbindAsync() {
        (Socket Listener, CancellationTokenSource Stop, Task Accepting, HashSet<Task> Connections) bound;

        // Disposing a bound socket unlinks its path, so it happens under the gate, where no later bind can have put a
        // fresh socket there yet.
        lock (_gate) {
            if (_bound is not { } current) return;

            bound  = current;
            _bound = null;
            bound.Listener.Dispose();
        }

        await bound.Stop.CancelAsync();
        await bound.Accepting;

        Task[] open;
        lock (bound.Connections) open = [.. bound.Connections];
        await Task.WhenAll(open);

        bound.Stop.Dispose();
        LogUnbound(socketPath);
    }

    public ValueTask DisposeAsync() => new(UnbindAsync());

    async Task AcceptAsync(Socket listener, SemaphoreSlim slots, HashSet<Task> connections, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            Socket connection;

            try {
                connection = await listener.AcceptAsync(ct);
            } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                             || ex is SocketException { SocketErrorCode: SocketError.OperationAborted or SocketError.Interrupted }) {
                return;
            } catch (SocketException ex) {
                LogAcceptFailed(ex);

                try { await Task.Delay(AcceptRetry, time, ct); } catch (OperationCanceledException) { return; }

                continue;
            }

            if (!slots.Wait(0, CancellationToken.None)) {
                LogBusy(MaxConnections);
                connection.Dispose();

                continue;
            }

            var serving = Task.Run(() => ServeAsync(connection, slots, ct), CancellationToken.None);

            lock (connections) connections.Add(serving);
            _ = serving.ContinueWith(done => { lock (connections) connections.Remove(done); }, TaskScheduler.Default);
        }
    }

    async Task ServeAsync(Socket connection, SemaphoreSlim slots, CancellationToken ct) {
        try {
            await using var stream = new NetworkStream(connection, ownsSocket: true);

            if (await AnswerAsync(stream, ct) is not { } response) return;

            using var deadline = new CancellationTokenSource(RequestDeadline, time);
            using var writing  = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

            await response.WriteAsync(stream, writing.Token);
            connection.Shutdown(SocketShutdown.Send);
        } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) {
            // Past the deadline, cut off by an unbind, or the client went away: there is no one to answer.
        } catch (Exception ex) {
            LogConnectionFaulted(ex);
        } finally {
            slots.Release();
        }
    }

    /// <summary>The answer to a whole request, or null when none arrived.</summary>
    async Task<SteeringHttpResponse?> AnswerAsync(Stream stream, CancellationToken ct) {
        SteeringHttpRequest? request;

        using (var deadline = new CancellationTokenSource(RequestDeadline, time))
        using (var reading = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token)) {
            try {
                request = await SteeringHttpReader.ReadAsync(stream, MaxHeaderBytes, MaxBodyBytes, reading.Token);
            } catch (SteeringHttpRefusal refusal) {
                LogRefused(refusal.Status, refusal.Code);

                return SteeringHttpResponse.Error(refusal.Status, refusal.Code);
            }
        }

        if (request is null) return null;
        if (!postRoutes.TryGetValue(request.Path, out var route)) return SteeringHttpResponse.Error(404, "not_found");
        if (request.Method != "POST") return SteeringHttpResponse.Error(405, "method_not_allowed");

        try {
            return await route(request, ct);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            LogRouteFaulted(request.Path, ex);

            return SteeringHttpResponse.Error(500, "internal_error");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Steering socket listening at {Path}")]
    partial void LogBound(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Steering socket at {Path} closed")]
    partial void LogUnbound(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steering socket could not bind at {Path}: {Reason}")]
    partial void LogBindFailed(string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steering socket accept failed")]
    partial void LogAcceptFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Steering connection dropped: {Max} already open")]
    partial void LogBusy(int max);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Steering request refused with {Status} ({Code})")]
    partial void LogRefused(int status, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steering route {Path} faulted")]
    partial void LogRouteFaulted(string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steering connection faulted")]
    partial void LogConnectionFaulted(Exception ex);
}
