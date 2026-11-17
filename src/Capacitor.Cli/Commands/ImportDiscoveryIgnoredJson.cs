namespace Capacitor.Cli.Commands;

/// <summary>Sessions the profile's capture scope (<c>kcap ignore</c>, <c>excluded_repos</c>, the allow lists) keeps out
/// of any import, in <c>kcap import --discover --json</c>.</summary>
/// <param name="Folders">Most sessions first; a null folder counts sessions with none recorded.</param>
public sealed record ImportDiscoveryIgnoredJson(int Count, IReadOnlyList<ImportIgnoredFolder> Folders);
