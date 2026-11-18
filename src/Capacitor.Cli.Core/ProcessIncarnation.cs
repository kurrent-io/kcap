namespace Capacitor.Cli.Core;

/// <summary>One run of a process: its pid, and the start token that tells it apart from a later process given the same
/// pid. Looking at it never signals it.</summary>
public sealed record ProcessIncarnation(int Pid, string StartToken) {
    /// <summary>The process running as <paramref name="pid"/> now, or null when there is none or its token can't be read:
    /// a pid with no token can't be told from its successor.</summary>
    public static ProcessIncarnation? Of(int pid) => pid > 1 && ProcessStartToken.ForPid(pid) is { } token ? new(pid, token) : null;

    /// <summary>Conclusive only: a process whose identity can't be compared still counts as this one.</summary>
    public bool HasExited() => WatcherLiveness.IsGone(Pid, StartToken);
}
