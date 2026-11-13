namespace Capacitor.Cli.Harness.Copilot;

/// <summary>
/// The end a Copilot session watcher sends when something asks it to stop. Quitting VS Code terminates what it started,
/// watchers included, and a session whose sessionEnd hook Copilot skipped has no other owner for its end.
/// </summary>
public static class CopilotStopRequestEnd {
    /// <summary>How long a stopping watcher waits for the runtime to exit or write <c>session.shutdown</c>.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    /// <summary>The reason to post, or null while nothing shows the session over.</summary>
    public static async Task<string?> DecideAsync(Func<bool> runtimeGone, Func<bool> endsOnShutdown, TimeProvider time, TimeSpan wait) {
        var deadline = time.GetUtcNow() + wait;

        while (true) {
            if (runtimeGone()) return "parent_exited";
            if (endsOnShutdown()) return "session_shutdown";
            if (time.GetUtcNow() >= deadline) return null;

            await Task.Delay(Poll, time);
        }
    }
}
