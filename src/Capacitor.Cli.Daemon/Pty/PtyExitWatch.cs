namespace Capacitor.Cli.Daemon.Pty;

/// <summary>When a terminal reader asks whether its child has exited, and how long it reads on after. A descendant
/// that inherited the terminal keeps it open after the child dies and may keep writing, so neither the end of
/// output nor an idle terminal can be waited for.</summary>
/// <param name="childExited">Reaps the child if it has exited.</param>
sealed class PtyExitWatch(Func<bool> childExited, TimeProvider time) {
    internal static readonly TimeSpan CheckGap   = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(1);

    long _checkedAt = time.GetTimestamp();
    long _goneAt;

    public bool ChildGone { get; private set; }

    /// <summary>Whether the reader has read on for <see cref="DrainLimit"/> since the exit was seen.</summary>
    public bool DrainOver => ChildGone && time.GetElapsedTime(_goneAt) >= DrainLimit;

    /// <summary>For a terminal with nothing to read.</summary>
    public void Check() {
        if (ChildGone) return;

        _checkedAt = time.GetTimestamp();

        if (!childExited()) return;

        ChildGone = true;
        _goneAt   = _checkedAt;
    }

    /// <summary>For a terminal with output waiting, which may never go idle.</summary>
    public void CheckIfDue() {
        if (!ChildGone && time.GetElapsedTime(_checkedAt) >= CheckGap) Check();
    }
}
