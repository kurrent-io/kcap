using System.Globalization;
using Capacitor.App.Views;
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.Services;

/// A pull request or issue page on github.com, as a chat link names it. Its other pages (files,
/// commits, discussions), another host and a shorthand reference are not one.
public sealed record GitHubLink(string Url, GitHubLinkKind Kind, string Owner, string Repo, int Number) {
    public string Label => $"{Owner}/{Repo}#{Number.ToString(CultureInfo.InvariantCulture)}";

    /// Pull requests and issues share one number sequence per repository, and GitHub reads owner and
    /// repository without regard to case, so a pull request linked as an issue is the same item.
    public string Key => $"{Owner}/{Repo}#{Number.ToString(CultureInfo.InvariantCulture)}".ToLowerInvariant();

    /// The chip a chat link shows as: one whose label is its own address. A label the writer chose stays a link.
    public static GitHubLink? ChipFor(LinkInline link) =>
        !link.IsImage && LinkPolicy.IsOpenable(link.Url) && (link.IsAutoLink || KcapMarkdownExtension.Label(link) == link.Url) ? Parse(link.Url) : null;

    public static GitHubLink? Parse(string? url) {
        if (url is not { Length: > 0 and <= 4096 } || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri is not { Scheme: "https" or "http", UserInfo.Length: 0, IsDefaultPort: true } || uri.Host is not ("github.com" or "www.github.com"))
            return null;
        var path = uri.AbsolutePath;
        if (path.EndsWith('/')) path = path[..^1];
        if (path.Split('/') is not ["", var owner, var repo, var page, var number]) return null;
        var kind = page switch {
            "pull" => GitHubLinkKind.PullRequest,
            "issues" => GitHubLinkKind.Issue,
            _ => (GitHubLinkKind?)null,
        };
        if (kind is null || !GitHubCliRunner.ValidOwner(owner) || !GitHubCliRunner.ValidRepo(repo) || number.StartsWith('0')
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        return new GitHubLink(url, kind.Value, owner, repo, n);
    }
}
