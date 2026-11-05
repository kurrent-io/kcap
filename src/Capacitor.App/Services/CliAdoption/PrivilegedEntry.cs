namespace Capacitor.App.Services.CliAdoption;

/// <summary>An entry the privileged helper deletes, under the aside name <see cref="AsideRetirement.Plan"/> recorded.
/// With a <paramref name="Marker"/>, a path relative to the entry, the aside must be a directory holding it as a regular
/// file that contains every one of <paramref name="MarkerContains"/>. Without one, the aside must only still be the kind
/// of entry, link, directory or file, it was when the helper started.</summary>
public sealed record PrivilegedEntry(AsideEntry Entry, string? Marker, IReadOnlyList<string> MarkerContains);
