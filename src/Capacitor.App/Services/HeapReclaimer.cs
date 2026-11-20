namespace Capacitor.App.Services;

/// <summary>Runs one aggressive, compacting collection once the app settles after a large transcript is dropped.
/// An idle UI rarely reaches a gen2 on its own, so without this the freed transcript's segments stay committed
/// and the footprint stays at its peak until the next large open reuses them.</summary>
public sealed class HeapReclaimer(TimeProvider time, Action collect) : IDisposable {
    public const int LineThreshold = 2_000;

    /// Long enough that the blocking collection lands after the next session's first render, not during it.
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    public static HeapReclaimer Shared { get; } = new(TimeProvider.System, Collect);

    readonly ITimer _timer = time.CreateTimer(_ => collect(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>Notes that a transcript of <paramref name="lines"/> lines was let go. Each large drop restarts the
    /// settle window, so closing several tabs in a row costs one collection.</summary>
    public void Released(long lines) {
        if (lines < LineThreshold) return;
        _timer.Change(Settle, Timeout.InfiniteTimeSpan);
    }

    // Aggressive is the mode that also decommits the freed regions; a plain forced gen2 compacts but keeps them.
    static void Collect() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    public void Dispose() => _timer.Dispose();
}
