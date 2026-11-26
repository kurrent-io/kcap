using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Lucide.Avalonia;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// A chip names its item as `owner/repo#n` with the pull request reader's lifecycle glyph, and says
/// the state in words once it is known; an issue is only ever Open or Closed.
public class GitHubLinkChipViewModelTests {
    static readonly GitHubLink PullRequest = GitHubLink.Parse("https://github.com/kurrent-io/kcap/pull/2625")!;
    static readonly GitHubLink Issue = GitHubLink.Parse("https://github.com/kurrent-io/kcap/issues/2625")!;

    [Test]
    [Arguments(GitHubLinkState.Open, "Open", LucideIconKind.GitPullRequest)]
    [Arguments(GitHubLinkState.Draft, "Draft", LucideIconKind.GitPullRequestDraft)]
    [Arguments(GitHubLinkState.Merged, "Merged", LucideIconKind.GitMerge)]
    [Arguments(GitHubLinkState.Closed, "Closed", LucideIconKind.CircleX)]
    public async Task A_pull_request_says_its_state(GitHubLinkState state, string word, LucideIconKind glyph) {
        var chip = new GitHubLinkChipViewModel(PullRequest, state);

        await Assert.That(chip.Label).IsEqualTo("kurrent-io/kcap#2625");
        await Assert.That(chip.StatusText).IsEqualTo(word);
        await Assert.That(chip.Glyph).IsEqualTo(glyph);
        await Assert.That(chip.AccessibleName).IsEqualTo($"Pull request kurrent-io/kcap#2625, {word}");
    }

    [Test]
    [Arguments(GitHubLinkState.Open, "Open")]
    [Arguments(GitHubLinkState.Closed, "Closed")]
    [Arguments(GitHubLinkState.Draft, "Open")]
    [Arguments(GitHubLinkState.Merged, "Closed")]
    public async Task An_issue_is_open_or_closed(GitHubLinkState state, string word) {
        var chip = new GitHubLinkChipViewModel(Issue, state);

        await Assert.That(chip.StatusText).IsEqualTo(word);
        await Assert.That(chip.Glyph).IsEqualTo(LucideIconKind.CircleDot);
        await Assert.That(chip.AccessibleName).IsEqualTo($"Issue kurrent-io/kcap#2625, {word}");
    }

    [Test]
    public async Task Without_a_state_the_chip_is_its_glyph_and_label_alone() {
        var pull = new GitHubLinkChipViewModel(PullRequest, null);
        var issue = new GitHubLinkChipViewModel(Issue, null);

        await Assert.That(pull.HasStatus).IsFalse();
        await Assert.That(pull.Glyph).IsEqualTo(LucideIconKind.GitPullRequest);
        await Assert.That(pull.AccessibleName).IsEqualTo("Pull request kurrent-io/kcap#2625");
        await Assert.That(issue.HasStatus).IsFalse();
        await Assert.That(issue.Glyph).IsEqualTo(LucideIconKind.CircleDot);
        await Assert.That(issue.AccessibleName).IsEqualTo("Issue kurrent-io/kcap#2625");
    }
}
