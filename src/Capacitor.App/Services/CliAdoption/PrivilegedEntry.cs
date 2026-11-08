namespace Capacitor.App.Services.CliAdoption;

/// <summary>An entry the privileged helper deletes, under the aside name <see cref="AsideRetirement.Plan"/> recorded.
/// Its aside must be of <paramref name="Kind"/>. With a <paramref name="Marker"/>, a path relative to a directory entry,
/// the aside must also hold it as a regular file containing every one of <paramref name="MarkerContains"/>, of which
/// there must be at least one. Only a script install's own <c>versions</c>, <c>current</c> and <c>install.json</c> may
/// go without a marker, each as the kind that layout gives it.</summary>
/// <param name="Pinned">For a directory: a file's path inside it and the identity <see cref="EntryIdentity"/> recorded
/// for it, which a reinstall at the same path changes.</param>
/// <param name="LinkTargetName">For a link: the last segment its target must have, the version a script install's
/// <c>current</c> names, which an update moves on.</param>
public sealed record PrivilegedEntry(
    AsideEntry Entry, PrivilegedEntryKind Kind, string? Marker, IReadOnlyList<string> MarkerContains,
    (string Path, string Identity)? Pinned = null, string? LinkTargetName = null) {
    /// <summary>Whether the entry, or the aside it was set aside under and never checked, was there when the app planned
    /// it: only one that was not may be missing for having been deleted already.</summary>
    public bool Present { get; init; } = true;

    /// <summary>For a directory, every name it held when the app planned it: one more, such as a version an update has
    /// staged, moves the install back.</summary>
    public IReadOnlyList<string>? Children { get; init; }
}
