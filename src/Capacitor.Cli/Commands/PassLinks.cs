namespace Capacitor.Cli.Commands;

static class PassLinks {
    /// <summary>The sender's Sent list in kcap, where a pass awaiting approval is approved or declined.</summary>
    public static string Sent(string serverUrl) => $"{serverUrl.TrimEnd('/')}/passes?tab=sent";
}
