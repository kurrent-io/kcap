namespace Capacitor.Cli.Recording;

/// <summary>One attempt at recording a source: when it last beat, and whether its host has given up on it. Once it has,
/// a beat does nothing at all, so a recorder that wakes behind the next attempt keeps no claim alive.</summary>
sealed class RecorderAttempt(TimeProvider time) {
    long   _beatAt = time.GetTimestamp();
    long   _excusedAt;
    int    _superseded;
    Action _beats = static () => { };
    Action _claim = static () => { };

    public bool Superseded => Volatile.Read(ref _superseded) != 0;

    /// <summary>The timestamp of its last beat, or of its start.</summary>
    public long BeatAt => Volatile.Read(ref _beatAt);

    /// <summary>How long since its last beat, an excused stretch included.</summary>
    public TimeSpan SinceBeat => time.GetElapsedTime(BeatAt);

    /// <summary>How long it has gone without a beat that is held against it.</summary>
    TimeSpan Quiet => time.GetElapsedTime(Math.Max(BeatAt, Volatile.Read(ref _excusedAt)));

    /// <summary>What a beat beats besides this attempt, and the claim part of that, which an excuse keeps up too. Its
    /// context is built with <see cref="Beat"/> in hand, so this comes after.</summary>
    public void BeatsWith(Action beats, Action claim) {
        _beats = beats;
        _claim = claim;
    }

    public void Beat() {
        if (Superseded) return;

        Volatile.Write(ref _beatAt, time.GetTimestamp());
        _beats();
    }

    /// <summary>Holds none of the time so far against the attempt. Of what a beat beats, only the claim: one its owner
    /// keeps through an outage would otherwise go stale under an attempt waiting that outage out.</summary>
    public void Excuse() {
        if (Superseded) return;

        Volatile.Write(ref _excusedAt, time.GetTimestamp());
        _claim();
    }

    public void Supersede() => Volatile.Write(ref _superseded, 1);

    /// <summary>Waits for <paramref name="work"/>, or until this attempt goes <paramref name="limit"/> without a beat.
    /// <see cref="Timeout.InfiniteTimeSpan"/> waits for the work alone.</summary>
    /// <returns>False on the latter, with the work still running.</returns>
    public async Task<bool> EndsBeatingAsync(Task work, TimeSpan limit) {
        if (limit == Timeout.InfiniteTimeSpan) {
            await work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            return true;
        }

        while (!work.IsCompleted) {
            var left = limit - Quiet;
            if (left <= TimeSpan.Zero) return work.IsCompleted;

            await work.WaitAsync(left, time).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        return true;
    }
}
