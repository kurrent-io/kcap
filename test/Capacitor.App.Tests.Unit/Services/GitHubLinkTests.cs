using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit.Services;

/// A github.com pull request or issue page reads as one, however the address is dressed; any other
/// page, host or shorthand does not.
public class GitHubLinkTests {
    [Test]
    [Arguments("https://github.com/kurrent-io/kcap/pull/2625", GitHubLinkKind.PullRequest)]
    [Arguments("https://github.com/kurrent-io/kcap/issues/2625", GitHubLinkKind.Issue)]
    [Arguments("https://www.github.com/kurrent-io/kcap/pull/2625", GitHubLinkKind.PullRequest)]
    [Arguments("https://GitHub.com/kurrent-io/kcap/issues/2625", GitHubLinkKind.Issue)]
    [Arguments("https://github.com/kurrent-io/kcap/pull/2625/", GitHubLinkKind.PullRequest)]
    [Arguments("https://github.com/kurrent-io/kcap/pull/2625?notification_referrer_id=abc", GitHubLinkKind.PullRequest)]
    [Arguments("https://github.com/kurrent-io/kcap/issues/2625#issuecomment-1", GitHubLinkKind.Issue)]
    [Arguments("https://github.com/kurrent-io/kcap/pull/2625/?w=1#discussion_r9", GitHubLinkKind.PullRequest)]
    [Arguments("http://www.github.com/kurrent-io/kcap/issues/2625", GitHubLinkKind.Issue)]
    public async Task A_pull_request_or_issue_page_is_a_link_to_it(string url, GitHubLinkKind kind) {
        await Assert.That(GitHubLink.Parse(url)).IsEqualTo(new GitHubLink(url, kind, "kurrent-io", "kcap", 2625));
    }

    [Test]
    public async Task Its_label_is_owner_repo_and_number_as_written() {
        var link = GitHubLink.Parse("https://github.com/Kurrent-IO/KCap.Web/pull/7")!;

        await Assert.That(link.Label).IsEqualTo("Kurrent-IO/KCap.Web#7");
    }

    [Test]
    public async Task A_pull_request_and_an_issue_with_one_number_are_one_item() {
        var pull = GitHubLink.Parse("https://github.com/Kurrent-IO/kcap/pull/7")!;
        var issue = GitHubLink.Parse("https://github.com/kurrent-io/KCAP/issues/7")!;

        await Assert.That(pull.Key).IsEqualTo(issue.Key);
    }

    [Test]
    [Arguments("https://github.com/kurrent-io/kcap/pull/2625/files")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/2625/commits")]
    [Arguments("https://github.com/kurrent-io/kcap/commit/c99a6235c")]
    [Arguments("https://github.com/kurrent-io/kcap/discussions/12")]
    [Arguments("https://github.com/kurrent-io/kcap/blob/main/README.md")]
    [Arguments("https://github.com/kurrent-io/kcap/pulls")]
    [Arguments("https://github.com/kurrent-io/kcap/issues")]
    [Arguments("https://github.com/kurrent-io/kcap/issues/new")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/0")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/012")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/99999999999")]
    [Arguments("https://github.com/kurrent-io/kcap")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/12//")]
    [Arguments("https://github.example.com/kurrent-io/kcap/pull/12")]
    [Arguments("https://gist.github.com/kurrent-io/kcap/pull/12")]
    [Arguments("https://api.github.com/repos/kurrent-io/kcap/pulls/12")]
    [Arguments("https://github.com.evil.example/kurrent-io/kcap/pull/12")]
    [Arguments("https://user@github.com/kurrent-io/kcap/pull/12")]
    [Arguments("https://github.com:8443/kurrent-io/kcap/pull/12")]
    [Arguments("ftp://github.com/kurrent-io/kcap/pull/12")]
    [Arguments("https://github.com/-kurrent/kcap/pull/12")]
    [Arguments("https://github.com/kurrent-io/../pull/12")]
    [Arguments("#123")]
    [Arguments("kurrent-io/kcap#123")]
    [Arguments("")]
    [Arguments(null)]
    public async Task Any_other_address_is_not(string? url) {
        await Assert.That(GitHubLink.Parse(url)).IsNull();
    }
}
