namespace Capacitor.Client;

public static class WorkContextTiming {
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// The server answers a read from a projection that can trail the write that prompted it, so a
    /// write is read twice: at once, and again after this long.
    public static readonly TimeSpan PlanSettleDelay = TimeSpan.FromSeconds(2);
}
