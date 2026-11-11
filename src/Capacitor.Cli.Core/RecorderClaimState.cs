namespace Capacitor.Cli.Core;

/// <summary>What a source's claim says about who records it. <see cref="None"/> leaves it to the watcher a live pid
/// file names, if any.</summary>
public enum RecorderClaimState {
    None,
    DaemonFresh,
    DaemonStale,
}
