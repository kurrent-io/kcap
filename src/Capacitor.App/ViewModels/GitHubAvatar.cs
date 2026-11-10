using System.Globalization;

namespace Capacitor.App.ViewModels;

public static class GitHubAvatar {
    /// A picture only for a `github:{n}` id; any other id has no URL to derive.
    public static string? UrlFor(string? userId) =>
        userId is not null && userId.StartsWith("github:", StringComparison.Ordinal)
        && long.TryParse(userId.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? $"https://avatars.githubusercontent.com/u/{id}?v=4&s=64" : null;
}
