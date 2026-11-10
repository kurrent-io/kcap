namespace Capacitor.Cli.Core.Tests.Unit.Workspace;

/// <summary>Real time, except that the timestamp reads <paramref name="stalls"/> picks (counted from 1)
/// read an hour later: a budget that runs out at a chosen step of the work. Its timers never fire, so a
/// slow host cannot time a step out and move where the budget runs out.</summary>
sealed class StallingClock(Func<int, bool> stalls) : TimeProvider {
    int _reads;

    public override long GetTimestamp() {
        var now = base.GetTimestamp();
        return stalls(Interlocked.Increment(ref _reads)) ? now + (long) (TimestampFrequency * 3600) : now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Silent();

    sealed class Silent : ITimer {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
