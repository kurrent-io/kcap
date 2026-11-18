namespace Capacitor.Cli.Daemon.Pty;

/// <summary>When a terminal reader asks whether its child has exited, and how long it reads on after. A descendant
/// that inherited the terminal keeps it open after the child dies and may keep writing, so neither the end of
/// output nor an idle terminal can be waited for.</summary>
/// <param name="childExited">Whether the child has exited.</param>
sealed class PtyExitWatch(Func<bool> childExited, TimeProvider time) {
    internal static readonly TimeSpan CheckGap   = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(1);

    long     _checkedAt = time.GetTimestamp();
    long     _readingSince;
    bool     _reading = true;
    TimeSpan _drained;

    public bool ChildGone { get; private set; }

    /// <summary>Whether the reader has spent <see cref="DrainLimit"/> reading since the exit was seen. Time spent
    /// handing over what it read is not counted: a slow consumer must not cut off output the child left.</summary>
    public bool DrainOver => ChildGone && Drained >= DrainLimit;

    TimeSpan Drained => _reading ? _drained + time.GetElapsedTime(_readingSince) : _drained;

    /// <summary>For a terminal with nothing to read.</summary>
    public void Check() {
        if (ChildGone) return;

        _checkedAt = time.GetTimestamp();

        if (!childExited()) return;

        ChildGone     = true;
        _readingSince = _checkedAt;
    }

    /// <summary>For a terminal with output waiting, which may never go idle.</summary>
    public void CheckIfDue() {
        if (!ChildGone && time.GetElapsedTime(_checkedAt) >= CheckGap) Check();
    }

    /// <summary>The reader is handing over a chunk and waiting for room for the next.</summary>
    public void Handing() {
        if (!_reading) return;

        if (ChildGone) _drained += time.GetElapsedTime(_readingSince);
        _reading = false;
    }

    /// <summary>The reader is back to reading.</summary>
    public void Reading() {
        if (_reading) return;

        _readingSince = time.GetTimestamp();
        _reading      = true;
    }
}
