using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Lists an ACP child's descendants while it lives, so they can still be killed once it has died and
/// they were reparented away from it: a vendor that re-execs itself leaves its real agent behind when
/// only the spawned wrapper dies. Every descendant is named by its start identity, so a recycled pid is
/// never signalled.
/// </summary>
internal sealed record AcpProcessTreeProbe(
        Func<int, IReadOnlyList<(int Pid, string Identity)>> Descendants,
        Action<int, string> Kill,
        Func<int, string, bool> IsAlive) {
    public static readonly AcpProcessTreeProbe Os = new(
        Descendants: pid => pid > 0 && ProcessStartToken.ForPid(pid) is { } identity ? ProcessTree.Descendants(pid, identity) : [],
        Kill:        ProcessTree.Kill,
        IsAlive:     (pid, identity) => ProcessStartToken.Matches(pid, identity) == true);
}
