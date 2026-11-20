using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// The reconnect ladder every client of <c>/hubs/sessions</c> shares: 1/2/5/10/30 s, then 30 s for as long as it takes.
/// One ladder is what makes the app, the recorder and the daemon come back together after a server restart; a client
/// on its own schedule shows stale state, or loses live transcript, until its next try.
/// </summary>
public sealed class HubRetryPolicy : IRetryPolicy {
    /// <summary>Half SignalR's default, so the socket stays warm through cloudflared. The server timeout stays at its
    /// 30 s default: a server mid-rollout may still ping at 15 s, and needs twice that.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(7);

    static readonly TimeSpan[] DefaultLadder = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    readonly TimeSpan[]             _ladder;
    readonly Func<Exception, bool>? _giveUpOn;

    /// <param name="giveUpOn">A failure no retry can repair, such as a refused credential: the connection closes on it
    /// instead of retrying, so its owner can act on the close.</param>
    /// <param name="ladder">Replaces the default delays; its last step repeats.</param>
    public HubRetryPolicy(Func<Exception, bool>? giveUpOn = null, TimeSpan[]? ladder = null) {
        if (ladder is { Length: 0 }) throw new ArgumentException("A ladder needs at least one step.", nameof(ladder));

        _ladder   = ladder ?? DefaultLadder;
        _giveUpOn = giveUpOn;
    }

    /// <summary>The pause before the attempt that follows <paramref name="previousAttempts"/> failed ones.</summary>
    public TimeSpan Delay(long previousAttempts) => _ladder[(int)Math.Min(previousAttempts, _ladder.Length - 1)];

    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        retryContext.RetryReason is { } reason && _giveUpOn?.Invoke(reason) == true
            ? null
            : Delay(retryContext.PreviousRetryCount);
}
