using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// <summary>
/// A fake clock that records every due time armed on it, by a new timer or a timer's <c>Change</c>, so a test advances
/// it only once the code under test is waiting. Advancing before the wait is armed moves the clock past nothing, and on
/// a loaded host a fixed real-time pause before the advance does not guarantee the wait is armed yet.
/// </summary>
public sealed class ArmedTimeProvider : FakeTimeProvider {
    readonly List<TimeSpan> _armed = [];

    /// <summary>How many waits have been armed so far: a mark for <see cref="ArmedSince"/>.</summary>
    public int Armed {
        get { lock (_armed) return _armed.Count; }
    }

    /// <summary>Whether a wait of exactly <paramref name="due"/> has been armed since <paramref name="mark"/>.</summary>
    public bool ArmedSince(int mark, TimeSpan due) {
        lock (_armed) return _armed.Skip(mark).Contains(due);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
        // Recorded once the timer exists, so a test that sees the record and advances fires it.
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Record(dueTime);
        return new RecordingTimer(timer, this);
    }

    void Record(TimeSpan due) {
        if (due == Timeout.InfiniteTimeSpan) return;
        lock (_armed) _armed.Add(due);
    }

    sealed class RecordingTimer(ITimer inner, ArmedTimeProvider clock) : ITimer {
        public bool Change(TimeSpan dueTime, TimeSpan period) {
            var changed = inner.Change(dueTime, period);
            clock.Record(dueTime);
            return changed;
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
