namespace Capacitor.Cli.Recording;

/// <summary>A beat for a loop as long as its input, at most once a second: a watcher's beat writes a file, and a loop over a
/// large backlog must show it is getting through it without doing that on every line.</summary>
sealed class ThrottledBeat(Action beat, TimeProvider time) {
    static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    long _last = time.GetTimestamp();

    public void Tick() {
        if (time.GetElapsedTime(_last) < Every) return;

        _last = time.GetTimestamp();
        beat();
    }
}
