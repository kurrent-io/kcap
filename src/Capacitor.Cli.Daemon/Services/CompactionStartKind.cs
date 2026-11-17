namespace Capacitor.Cli.Daemon.Services;

/// <summary>What a runtime did with a request to compact its agent's context.</summary>
internal enum CompactionStartKind {
    Started,
    /// <summary>A prompt is open that the compaction could reach.</summary>
    PromptOpen,
    Unsupported,
}
