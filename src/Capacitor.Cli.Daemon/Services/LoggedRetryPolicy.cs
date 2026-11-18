using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Writes one line per reconnect attempt, with the reason and no stack trace: a server restarting or
/// upgrading is expected, and the client's own lines for it are held at Debug by <see cref="ReconnectQuietLoggerFactory"/>.</summary>
sealed partial class LoggedRetryPolicy(IRetryPolicy inner, ILogger logger) : IRetryPolicy {
    public TimeSpan? NextRetryDelay(RetryContext retryContext) {
        var delay  = inner.NextRetryDelay(retryContext);
        var reason = Reason(retryContext.RetryReason);

        if (delay is not { } next) LogGivingUp(retryContext.PreviousRetryCount, reason);
        else if (retryContext.PreviousRetryCount == 0) LogConnectionLost(reason, next.TotalSeconds);
        else LogAttemptFailed(retryContext.PreviousRetryCount, reason, next.TotalSeconds);

        return delay;
    }

    /// <summary>The exception's own message on one line, which names the status or the refused endpoint.</summary>
    internal static string Reason(Exception? ex) =>
        ex is null ? "closed by the server" : ex.Message.ReplaceLineEndings(" ").Trim() is { Length: > 0 } message ? message : ex.GetType().Name;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hub connection lost ({Reason}); reconnecting in {Delay}s")]
    partial void LogConnectionLost(string reason, double delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconnect attempt {Attempt} failed ({Reason}); retrying in {Delay}s")]
    partial void LogAttemptFailed(long attempt, string reason, double delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconnect attempt {Attempt} failed ({Reason}); giving up")]
    partial void LogGivingUp(long attempt, string reason);
}
