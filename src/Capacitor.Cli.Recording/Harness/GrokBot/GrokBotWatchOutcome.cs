namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>Why a run of the watcher returned.</summary>
public enum GrokBotWatchOutcome {
    /// <summary>Cancelled by its caller.</summary>
    Stopped,

    /// <summary>A single cycle ran clean.</summary>
    Completed,

    /// <summary>A single cycle stopped short.</summary>
    Failed,

    /// <summary>This computer has neither a gateway nor a desktop app chat store.</summary>
    NoSource,

    /// <summary>Another watcher on this computer holds the lock.</summary>
    AlreadyRunning
}
