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

    static TimeoutException Stalled(string step) =>
        new($"Stalled waiting for {step}: not done within {DeadlineSeconds}s.");
}
