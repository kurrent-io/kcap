namespace Capacitor.Cli.Commands;

/// <summary>A daemon that holds up an uninstall, and why.</summary>
sealed record DaemonBusy(string Name, DaemonBusyReason Reason) {
    public string Line => Reason switch {
        DaemonBusyReason.Running => $"daemon '{Name}' is running or starting agents",
        DaemonBusyReason.Unknown => $"daemon '{Name}': it cannot say whether agents are running",
        _                        => $"daemon '{Name}' did not answer within {(int)OutgoingRecorders.StartingWait.TotalSeconds} s; it may still be starting",
    };

    public static string Lines(IEnumerable<DaemonBusy> busy) => string.Join("; ", busy.Select(b => b.Line));

    /// <summary>A daemon that never answered is not said to run agents: one still starting cannot answer yet.</summary>
    public static bool AnyMayRunAgents(IEnumerable<DaemonBusy> busy) => busy.Any(b => b.Reason != DaemonBusyReason.NoAnswer);
}
