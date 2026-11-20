namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Retries a hub invocation across transient connection drops, so a SignalR blip during a long
/// permission wait does not become a silent deny.
///
/// Every attempt waits until the connection is connected and re-registered. A cancellation from the
/// transport dropping, or "connection is not active", is retried until <c>ct</c>; the caller's own
/// shutdown cancellation propagates. Any other exception propagates, and the bridge denies.
///
/// <c>isRetriableServerError</c> with <c>maxServerErrorRetries</c> retries a bounded number of server
/// rejections, such as the brief "Caller is not the daemon owning session" after a reconnect, so a
/// permanent error still surfaces.
/// </summary>
internal static class ConnectionRetry {
    /// <summary>
    /// Non-generic overload for a hub invocation with no return value (e.g. <c>AcpSessionStarted</c>)
    /// — delegates to the generic overload with a discarded
    /// <see langword="object?"/> result so void-returning callers get the exact same gating/retry
    /// semantics as <see cref="InvokeWithConnectionRetryAsync{T}"/> without duplicating the loop.
    /// </summary>
    public static Task InvokeWithConnectionRetryAsync(
            Func<Task>             invoke,
            Func<bool>             isReady,
            TimeSpan               pollInterval,
            TimeProvider           time,
            Action<int>            onRetry,
            CancellationToken      ct,
            Func<Exception, bool>? isRetriableServerError = null,
            int                    maxServerErrorRetries = 0
        ) => InvokeWithConnectionRetryAsync<object?>(
            async () => { await invoke(); return null; },
            isReady,
            pollInterval,
            time,
            onRetry,
            ct,
            isRetriableServerError,
            maxServerErrorRetries
        );

    public static async Task<T> InvokeWithConnectionRetryAsync<T>(
            Func<Task<T>>      invoke,
            Func<bool>         isReady,
            TimeSpan           pollInterval,
            TimeProvider       time,
            Action<int>        onRetry,
            CancellationToken  ct,
            Func<Exception, bool>? isRetriableServerError = null,
            int                maxServerErrorRetries = 0
        ) {
        var serverErrorRetries = 0;

        for (var attempt = 1; ; attempt++) {
            // Wait for readiness before EVERY attempt, including the first. A
            // permission request can arrive while the daemon is mid-reconnect or
            // re-registering; invoking then could hit a connection the server
            // hasn't registered and surface as a HubException — a spurious deny.
            // Gating all attempts on readiness (not just post-failure retries)
            // closes that race.
            while (!ct.IsCancellationRequested && !isReady())
                await Task.Delay(pollInterval, time, ct);

            ct.ThrowIfCancellationRequested();

            try {
                return await invoke();
            } catch (Exception ex) when (!ct.IsCancellationRequested && IsTransientDisconnect(ex)) {
                onRetry(attempt);

                // Brief delay before looping back to the readiness wait, so the
                // loop can never spin hot even if isReady() flips true instantly.
                await Task.Delay(pollInterval, time, ct);
            } catch (Exception ex) when (!ct.IsCancellationRequested
                                      && isRetriableServerError is not null
                                      && serverErrorRetries < maxServerErrorRetries
                                      && isRetriableServerError(ex)) {
                serverErrorRetries++;
                onRetry(attempt);
                await Task.Delay(pollInterval, time, ct);
            }
        }
    }

    // We only ever invoke once the connection was observed ready, so an
    // InvalidOperationException ("connection is not active") here means the
    // transport dropped between the readiness check and the call — transient,
    // like the OperationCanceledException SignalR raises for an in-flight
    // invocation killed by a transport loss. HubException and any other
    // exception are not transient and propagate.
    static bool IsTransientDisconnect(Exception ex) =>
        ex is OperationCanceledException or InvalidOperationException;
}
