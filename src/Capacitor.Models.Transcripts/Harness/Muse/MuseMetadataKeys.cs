namespace Capacitor.Models.Transcripts.Harness.Muse;

/// The metadata keys Muse's stored events carry beside the common ones.
public static class MuseMetadataKeys {
    public const string Sequence   = "$muse_sequence";
    public const string RunId      = "$muse_run_id";
    public const string TaskId     = "$muse_task_id";
    public const string TurnFailed = "$muse_turn_failed";
}
