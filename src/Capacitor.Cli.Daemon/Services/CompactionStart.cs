namespace Capacitor.Cli.Daemon.Services;

/// <param name="Written">Completes once the runtime has sent the compaction on, and faults if it never will.</param>
/// <param name="Finished">Completes once the compaction is over; null where the runtime cannot tell.</param>
internal readonly record struct CompactionStart(CompactionStartKind Kind, Task? Written = null, Task? Finished = null) {
    public static CompactionStart Unsupported => new(CompactionStartKind.Unsupported);

    public static CompactionStart PromptOpen => new(CompactionStartKind.PromptOpen);

    public static CompactionStart Started(Task written, Task? finished) => new(CompactionStartKind.Started, written, finished);
}
