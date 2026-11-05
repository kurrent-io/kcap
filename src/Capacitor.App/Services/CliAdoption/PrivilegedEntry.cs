namespace Capacitor.App.Services.CliAdoption;

/// <summary>An entry the privileged helper deletes, under the aside name <see cref="AsideRetirement.Plan"/> recorded.
/// Its aside must be of <paramref name="Kind"/>. With a <paramref name="Marker"/>, a path relative to a directory entry,
/// the aside must also hold it as a regular file containing every one of <paramref name="MarkerContains"/>, of which
/// there must be at least one. Only a script install's own <c>versions</c>, <c>current</c> and <c>install.json</c> may
/// go without a marker, each as the kind that layout gives it.</summary>
public sealed record PrivilegedEntry(AsideEntry Entry, PrivilegedEntryKind Kind, string? Marker, IReadOnlyList<string> MarkerContains);
