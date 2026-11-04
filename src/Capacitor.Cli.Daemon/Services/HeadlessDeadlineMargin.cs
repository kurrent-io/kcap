namespace Capacitor.Cli.Daemon.Services;

/// <summary>The timeout a harness runner gets so that its own timeout, which keeps the usage reported so far, fires
/// before <see cref="HeadlessPromptRunner"/>'s outer deadline, which answers with none.</summary>
static class HeadlessDeadlineMargin {
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);

    /// <summary><see cref="Margin"/> short of the time left to <paramref name="deadline"/>, never more than
    /// <paramref name="timeout"/> and never below zero.</summary>
    public static TimeSpan RunnerTimeout(DateTimeOffset deadline, TimeSpan timeout, TimeProvider time) {
        var left = deadline - time.GetUtcNow() - Margin;

        return left < TimeSpan.Zero ? TimeSpan.Zero : left < timeout ? left : timeout;
    }
}
