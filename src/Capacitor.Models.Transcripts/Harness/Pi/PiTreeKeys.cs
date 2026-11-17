namespace Capacitor.Models.Transcripts.Harness.Pi;

/// The metadata keys a Pi event carries its tree node under. Pi branches inside one session file, so the
/// stored events interleave branches, and the server's branch picker rebuilds them from these keys.
public static class PiTreeKeys {
    public const string EntryId  = "$pi_entry_id";
    public const string ParentId = "$pi_parent_id";

    /// The nearest ancestor that produced an event. <see cref="ParentId"/> can name a control entry that
    /// produced none, which is why the picker detects forks by it but walks a branch by this.
    public const string RenderParent = "$pi_render_parent";
}
