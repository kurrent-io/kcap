namespace Capacitor.Cli.Daemon.Services;

/// <param name="Written">Completes once the runtime has sent the compaction on, and faults if it never will.</param>
/// <param name="Finished">Completes once the compaction is over; null where the runtime cannot tell.</param>
/// <param name="Begun">Completes once the runtime starts sending the compaction, past whatever was queued ahead of it;
/// null when it starts at once.</param>
internal readonly record struct CompactionStart(CompactionStartKind Kind, Task? Written = null, Task? Finished = null, Task? Begun = null) {
    public static CompactionStart Unsupported => new(CompactionStartKind.Unsupported);

    public static CompactionStart PromptOpen => new(CompactionStartKind.PromptOpen);

    public static CompactionStart Started(Task written, Task? finished, Task? begun = null) =>
        new(CompactionStartKind.Started, written, finished, begun);
}
