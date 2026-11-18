namespace Capacitor.Cli.Commands;

/// <summary>A working directory whose sessions the profile's capture scope keeps out of any import.
/// <paramref name="Folder"/> is null for sessions whose folder is not known.</summary>
public sealed record ImportIgnoredFolder(string? Folder, int Sessions);
