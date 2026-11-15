namespace Capacitor.Cli.Ide;

/// <param name="Key">Display text only: issues of two repositories can share it.</param>
internal sealed record WorkIssue(string Key, string Title, string? Url);
