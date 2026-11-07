namespace Capacitor.Cli.Core;

/// <summary>One record of <c>git status --porcelain=v1 -z</c>. <see cref="OriginalPath"/> is set only on
/// a rename or copy, and names the path the record moved or copied from.</summary>
public sealed record PorcelainEntry(string Code, string Path, string? OriginalPath = null);
