using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.RepoEvidence;

public class CreatedPullRequestEvidenceTests {
    const string Url = "https://github.com/kurrent-io/kcap/pull/1945";

    static readonly CreatedPullRequest Pr = new("kurrent-io", "kcap", 1945, Url);

    const string CreateOutput = "Creating pull request for feature into main in kurrent-io/kcap\n\n" + Url + "\n";

    [Test]
    public async Task A_paired_create_whose_output_ends_with_the_url_is_found() {
        var evidence = new CreatedPullRequestEvidence();

        evidence.Call("c1", "git push && gh pr create --fill");
        evidence.Result("c1", CreateOutput, succeeded: true);

        await Assert.That(evidence.Drain()).IsEquivalentTo([new FoundPullRequest(Pr, "c1")]);
        await Assert.That(evidence.Drain()).IsEmpty();
    }

    // gh prints the existing PR's URL on a create that fails because one exists; that PR may not be this session's.
    [Test]
    public async Task A_failed_create_is_not_found() {
        var evidence = new CreatedPullRequestEvidence();

        evidence.Call("c1", "gh pr create --fill");
        evidence.Result("c1", "a pull request for branch \"feature\" into branch \"main\" already exists:\n" + Url, succeeded: false);

        await Assert.That(evidence.Drain()).IsEmpty();
    }

    [Test]
    [Arguments("Opened " + Url + " for review")]
    [Arguments(Url + "\nWarning: 1 uncommitted change")]
    [Arguments(Url + " " + Url)]
    [Arguments("https://ghe.example.com/kurrent-io/kcap/pull/1945")]
    [Arguments(Url + "/files")]
    [Arguments("")]
    public async Task Output_whose_last_line_is_not_exactly_one_github_pr_url_is_not_found(string output) {
        var evidence = new CreatedPullRequestEvidence();

        evidence.Call("c1", "gh pr create --fill");
        evidence.Result("c1", output, succeeded: true);

        await Assert.That(evidence.Drain()).IsEmpty();
    }

    [Test]
    public async Task A_url_from_a_command_that_is_not_a_create_is_not_found() {
        var evidence = new CreatedPullRequestEvidence();

        evidence.Call("c1", "gh pr view --json url -q .url");
        evidence.Result("c1", Url, succeeded: true);
        evidence.Completed("c2", "echo " + Url, Url, succeeded: true);

        await Assert.That(evidence.Drain()).IsEmpty();
    }

    [Test]
    public async Task A_url_outside_the_named_repo_is_not_found() {
        var evidence = new CreatedPullRequestEvidence();

        evidence.Completed("c1", "gh pr create --repo kurrent-io/other --fill", Url, succeeded: true);
        evidence.Completed("c2", "gh pr create --repo kurrent-io/kcap --fill", Url, succeeded: true);

        await Assert.That(evidence.Drain()).IsEquivalentTo([new FoundPullRequest(Pr, "c2")]);
    }

    [Test]
    public async Task A_result_for_an_unknown_call_is_ignored() {
        var evidence = new CreatedPullRequestEvidence();

        evidence.Result("c1", Url, succeeded: true);

        await Assert.That(evidence.Drain()).IsEmpty();
    }

    [Test]
    public async Task Only_the_newest_pending_creates_are_kept() {
        var evidence = new CreatedPullRequestEvidence(capacity: 2);

        evidence.Call("c1", "gh pr create --fill");
        evidence.Call("c2", "gh pr create --fill");
        evidence.Call("c3", "gh pr create --fill");
        evidence.Result("c1", Url, succeeded: true);
        evidence.Result("c3", Url, succeeded: true);

        await Assert.That(evidence.Drain().Select(f => f.CallId ?? "")).IsEquivalentTo(["c3"]);
    }
}
