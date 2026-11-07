namespace Capacitor.Cli.Core.Tests.Unit.Workspace;

/// <summary>Real time for the first <paramref name="freeReads"/> timestamp reads, then an hour later
/// for every read after: a budget that runs out at a chosen step of the work. Its timers never fire,
/// so a slow host cannot time a step out and move where the budget runs out.</summary>
sealed class StallingClock(int freeReads) : TimeProvider {
    int _reads;

    public override long GetTimestamp() {
        var now = base.GetTimestamp();
        return Interlocked.Increment(ref _reads) <= freeReads ? now : now + (long) (TimestampFrequency * 3600);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Silent();

    sealed class Silent : ITimer {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
