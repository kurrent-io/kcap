namespace Capacitor.Cli.Core;

public static class BoundedTask {
    /// <summary>Whether <paramref name="task"/> ended inside <paramref name="limit"/>, by fault or cancellation
    /// included. Never throws the task's outcome, and observes a fault so it cannot surface later.</summary>
    public static async Task<bool> FinishedWithinAsync(this Task task, TimeSpan limit, TimeProvider time) {
        await task.WaitAsync(limit, time).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (!task.IsCompleted) return false;

        _ = task.Exception;

        return true;
    }
}
