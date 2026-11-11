namespace Capacitor.Cli;

public static class TimeBudget {
    /// <summary>
    /// Bound best-effort hook pre-work so an abandoned drain cannot consume the entire SessionEnd
    /// budget and starve its POST. Work left running after the cap must not control the returned result;
    /// server stop-and-drain and import recovery cover unfinished capture.
    /// </summary>
    public static async Task<bool> RunCappedAsync(Func<Task> work, TimeSpan cap, TimeProvider time) {
        var workTask = work();

        using var cts       = new CancellationTokenSource();
        var       delayTask = Task.Delay(cap, time, cts.Token);

        await Task.WhenAny(workTask, delayTask);
        await cts.CancelAsync(); // stop the timer either way

        // Observe the work whenever it has actually finished — even if the delay "won" a
        // near-boundary race — so a completed task's result/exception is never silently
        // dropped. Only abandon when the work is genuinely still running past the cap.
        if (workTask.IsCompleted) {
            await workTask; // propagate exception if faulted
            return true;
        }

        // Cap elapsed: abandon the work. Swallow any later fault so it doesn't surface as an
        // UnobservedTaskException — the hook process exits shortly after the POST anyway.
        _ = workTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        return false;
    }
}
