using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Patterns and paths are built from the unresolved temp root, which on a Mac sits behind the
/// <c>/var</c> → <c>/private/var</c> link: an allowed path reached through a link must still match.</summary>
public class RepoPathPatternTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    [Arguments("allowed/proj", true)]
    [Arguments("allowed", true)]
    [Arguments("allowed/proj/../other", true)]
    [Arguments("allowed/../outside", false)]
    [Arguments("allowed/proj/../../outside", false)]
    [Arguments("allowed-sibling", false)]
    public async Task A_glob_matches_what_the_path_resolves_to(string relative, bool expected) {
        Tmp.CreateDir("allowed", "proj");
        Tmp.CreateDir("allowed", "other");
        Tmp.CreateDir("outside");
        Tmp.CreateDir("allowed-sibling");

        var matches = RepoPathPattern.Matches(Tmp.PathTo("allowed") + "/*", Tmp.Path + "/" + relative);

        await Assert.That(matches).IsEqualTo(expected);
    }

    [Test]
    public async Task An_exact_pattern_refuses_traversal_that_lands_elsewhere() {
        var allowed = Tmp.CreateDir("allowed");
        Tmp.CreateDir("outside");

        await Assert.That(RepoPathPattern.Matches(allowed, Tmp.PathTo("outside", "..", "allowed"))).IsTrue();
        await Assert.That(RepoPathPattern.Matches(allowed, Tmp.PathTo("allowed", "..", "outside"))).IsFalse();
    }

    [Test]
    public async Task A_symlink_out_of_the_allowed_tree_is_refused_and_one_into_it_is_not() {
        var allowed = Tmp.CreateDir("allowed");
        var outside = Tmp.CreateDir("outside");
        var real    = allowed.CreateDir("proj");
        Directory.CreateSymbolicLink(allowed.PathTo("escape"), outside);
        Directory.CreateSymbolicLink(outside.PathTo("into"), real);

        var glob = allowed + "/*";

        await Assert.That(RepoPathPattern.Matches(glob, allowed.PathTo("escape"))).IsFalse();
        await Assert.That(RepoPathPattern.Matches(glob, allowed.PathTo("escape", "x"))).IsFalse();
        await Assert.That(RepoPathPattern.Matches(glob, outside.PathTo("into"))).IsTrue();
    }

    [Test]
    public async Task A_symlink_cycle_matches_nothing() {
        var allowed = Tmp.CreateDir("allowed");
        Directory.CreateSymbolicLink(allowed.PathTo("a"), allowed.PathTo("b"));
        Directory.CreateSymbolicLink(allowed.PathTo("b"), allowed.PathTo("a"));

        await Assert.That(RepoPathPattern.Matches(allowed + "/*", allowed.PathTo("a"))).IsFalse();
    }

    /// <summary>On a case-sensitive host <c>Repo</c> and <c>repo</c> are two repositories, so an entry
    /// naming one must refuse the other; the Linux CI leg is where the refusal is pinned.</summary>
    [Test]
    public async Task Case_variants_match_only_where_the_host_folds_case() {
        var foldsCase = PathComparison.Comparison == StringComparison.OrdinalIgnoreCase;
        var exact     = Tmp.PathTo("work", "repo");

        await Assert.That(RepoPathPattern.Matches(exact, Tmp.PathTo("work", "Repo"))).IsEqualTo(foldsCase);
        await Assert.That(RepoPathPattern.Matches(Tmp.PathTo("work") + "/*", Tmp.PathTo("Work", "repo"))).IsEqualTo(foldsCase);
        await Assert.That(RepoPathPattern.Matches(exact, exact)).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("/repo\0/x")]
    public async Task A_path_that_is_not_one_matches_nothing(string path) {
        await Assert.That(RepoPathPattern.Matches("/*", path)).IsFalse();
    }
}
