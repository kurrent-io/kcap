namespace Capacitor.Tests.Helpers;

/// <summary>
/// Bounds an await that a lost frame or a stalled peer could leave pending forever, so the test
/// fails naming the step it stalled on instead of hanging the assembly until the hang dump fires.
/// </summary>
public static class BoundedWait {
    /// <summary>A deadline, not a budget: it only has to outlast the slowest honest run on a loaded
    /// two-core runner, where work that finishes in milliseconds unloaded can take seconds.</summary>
    public const int DeadlineSeconds = 30;

    /// <inheritdoc cref="DeadlineSeconds"/>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(DeadlineSeconds);

    /// <param name="task">The await to bound.</param>
    /// <param name="step">What the test was waiting for, as it should read in the failure.</param>
    public static async Task WithinDeadlineAsync(this Task task, string step) {
        try {
            await task.WaitAsync(Deadline);
        } catch (TimeoutException) when (!task.IsCompleted) {
            throw Stalled(step);
        }
    }

    /// <inheritdoc cref="WithinDeadlineAsync(Task, string)"/>
    public static async Task<T> WithinDeadlineAsync<T>(this Task<T> task, string step) {
        try {
            return await task.WaitAsync(Deadline);
        } catch (TimeoutException) when (!task.IsCompleted) {
            throw Stalled(step);
        }
    }

    /// <inheritdoc cref="WithinDeadlineAsync(Task, string)"/>
    public static Task WithinDeadlineAsync(this ValueTask task, string step) => task.AsTask().WithinDeadlineAsync(step);

    /// <inheritdoc cref="WithinDeadlineAsync(Task, string)"/>
    public static Task<T> WithinDeadlineAsync<T>(this ValueTask<T> task, string step) => task.AsTask().WithinDeadlineAsync(step);

    /// <summary>
    /// Runs <paramref name="body"/>, then disposes <paramref name="resource"/> within the deadline. Use it
    /// instead of <c>await using</c> when disposal joins work the body may have left stuck: an implicit
    /// disposal would hang the test behind the very stall that failed it. When the body fails, its
    /// failure is the one reported, whatever the disposal does.
    /// </summary>
    /// <param name="disposal">What disposing waits for, as it should read in the failure.</param>
    public static async Task UsingWithinDeadlineAsync<T>(this T resource, Func<T, Task> body, string disposal)
        where T : IAsyncDisposable {
        try {
            await body(resource);
        } catch {
            try { await resource.DisposeAsync().WithinDeadlineAsync(disposal); } catch { }

            throw;
        }

        await resource.DisposeAsync().WithinDeadlineAsync(disposal);
    }

    static TimeoutException Stalled(string step) =>
        new($"Stalled waiting for {step}: not done within {DeadlineSeconds}s.");
}
