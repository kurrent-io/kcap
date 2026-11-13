namespace Capacitor.Cli.Ide;

/// <summary>Closes the host's services in order. A step that throws is logged and the next one still runs; once the
/// budget is spent, a step still closing is left to finish on its own and the next one is started without waiting.</summary>
internal static class CloseSteps {
    public static async Task RunAsync(
            IReadOnlyList<(string Name, Func<ValueTask> Close)> steps, TimeSpan budget, TimeProvider time, TextWriter log) {
        using var timeout = new CancellationTokenSource(budget, time);
        foreach (var (name, close) in steps) {
            try {
                await close().AsTask().WaitAsync(timeout.Token);
            } catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
                // The process exits without it.
            } catch (Exception ex) {
                await LogAsync(log, $"kcap ide: closing {name} failed: {ex}");
            }
        }
    }

    static async Task LogAsync(TextWriter log, string line) {
        try {
            await log.WriteLineAsync(line);
        } catch {
            // A broken stderr is not a reason to skip the remaining steps.
        }
    }
}
