namespace Capacitor.Tests.Helpers;

/// <summary>
/// A delay that records the span it was asked for and returns at once. Yields rather than completing
/// synchronously, so a poll answered "pending" forever still lets a cancellation in.
/// </summary>
public sealed class RecordingDelay {
    readonly List<TimeSpan> _requested = [];

    public IReadOnlyList<TimeSpan> Requested {
        get { lock (_requested) return [.. _requested]; }
    }

    public async Task DelayAsync(TimeSpan span, CancellationToken ct) {
        lock (_requested) _requested.Add(span);

        await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }
}
