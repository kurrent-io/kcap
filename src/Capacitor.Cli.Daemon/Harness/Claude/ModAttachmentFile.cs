namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>Everything about steering that outlives the daemon process. Dispatches the server pushed are not here: a
/// held one never survives its connection.</summary>
/// <param name="HandedOver">Keyed by dispatch id.</param>
/// <param name="Replaced">Agents a process carried on from, keyed by agent id, each naming that process.</param>
internal sealed record ModAttachmentFile(
        ModAttachmentRecord[]           Attachments,
        Dictionary<string, ModHandover> HandedOver,
        ModInputReport[]                Reports,
        Dictionary<string, string>      Replaced,
        ModStoppedPair[]                Stopped
    ) {
    public static ModAttachmentFile Empty => new([], [], [], [], []);
}
