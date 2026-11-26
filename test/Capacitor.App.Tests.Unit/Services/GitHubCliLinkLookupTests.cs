using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit.Services;

/// GitHub's issue endpoint answers for an issue and a pull request alike: open or closed, and for a
/// pull request whether it is a draft or was merged.
public class GitHubCliLinkLookupTests {
    [Test]
    [Arguments("""{"draft":null,"merged_at":null,"state":"open"}""", GitHubLinkState.Open)]
    [Arguments("""{"draft":false,"merged_at":null,"state":"open"}""", GitHubLinkState.Open)]
    [Arguments("""{"draft":true,"merged_at":null,"state":"open"}""", GitHubLinkState.Draft)]
    [Arguments("""{"draft":false,"merged_at":"2026-11-25T22:16:04Z","state":"closed"}""", GitHubLinkState.Merged)]
    [Arguments("""{"draft":true,"merged_at":null,"state":"closed"}""", GitHubLinkState.Closed)]
    [Arguments("""{"draft":null,"merged_at":null,"state":"closed"}""", GitHubLinkState.Closed)]
    public async Task The_answer_reads_as_the_state(string json, GitHubLinkState state) {
        await Assert.That(GitHubCliLinkLookup.Parse(json)).IsEqualTo(state);
    }

    [Test]
    [Arguments("""{"state":"locked"}""")]
    [Arguments("""{"draft":true}""")]
    [Arguments("""["open"]""")]
    [Arguments("open")]
    [Arguments("")]
    public async Task Anything_else_has_no_state(string json) {
        await Assert.That(GitHubCliLinkLookup.Parse(json)).IsNull();
    }
}
