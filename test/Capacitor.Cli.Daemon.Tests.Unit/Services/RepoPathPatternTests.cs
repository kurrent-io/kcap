using TUnit.Core.Enums;
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

    /// <summary>The walk itself is pinned in <c>CanonicalPathTests</c>; here a relative path is refused
    /// whatever it resolves to, and a relative pattern admits nothing.</summary>
    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task A_relative_path_through_a_symlink_and_dot_dot_is_refused() {
        var allowed = Tmp.CreateDir("allowed");
        var parent  = Tmp.CreateDir("outside", "parent");
        Tmp.CreateDir("outside", "secret");
        Directory.CreateSymbolicLink(allowed.PathTo("link"), parent);
#pragma warning disable RS0030 // a relative path means the working directory, which is the point
        var relativeAllowed = Path.GetRelativePath(Environment.CurrentDirectory, Tmp.GetResolvedPath("allowed"));
#pragma warning restore RS0030

        await Assert.That(Path.IsPathRooted(relativeAllowed)).IsFalse();
        await Assert.That(RepoPathPattern.Matches(allowed + "/*", Path.Combine(relativeAllowed, "link", "..", "secret"))).IsFalse();
        await Assert.That(RepoPathPattern.Matches(allowed + "/*", Path.Combine(relativeAllowed, "proj"))).IsFalse();
        await Assert.That(RepoPathPattern.Matches(relativeAllowed + "/*", allowed.PathTo("proj"))).IsFalse();
        await Assert.That(RepoPathPattern.Matches(allowed + "/*", allowed.PathTo("proj"))).IsTrue();
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task A_backslash_in_a_posix_name_is_not_a_separator() {
        var allowed = Tmp.CreateDir("allowed");
        allowed.CreateDir("secret");
        var beside = Tmp.CreateDir("allowed\\secret");

        await Assert.That(Path.GetDirectoryName(beside)).IsEqualTo(Tmp.Path);
        await Assert.That(RepoPathPattern.Matches(allowed + "/*", beside)).IsFalse();
        await Assert.That(RepoPathPattern.Matches(allowed.PathTo("secret"), beside)).IsFalse();
        await Assert.That(RepoPathPattern.Matches(allowed + "/*", allowed.PathTo("secret"))).IsTrue();
    }

    /// <summary>A Windows path can name a directory by its 8.3 alias, which no link resolution expands; a
    /// request spelled that way must still match the entry naming the directory in full.</summary>
    [Test]
    [RunOn(OS.Windows)]
    public async Task A_short_name_request_matches_the_long_entry_for_the_same_directory() {
        var allowed = Tmp.CreateDir("Allowed Repositories");
        var project = allowed.CreateDir("project with a long name");
        var shortProject = ShortNameOf(project);

        Skip.When(string.Equals(Path.GetFileName(shortProject), "project with a long name", StringComparison.OrdinalIgnoreCase),
            "8.3 short names are disabled on this volume, so no short spelling exists to test");

        await Assert.That(RepoPathPattern.Matches(Tmp.PathTo("Allowed Repositories") + "\\*", shortProject)).IsTrue();
        await Assert.That(RepoPathPattern.Matches(project, shortProject)).IsTrue();
        await Assert.That(RepoPathPattern.Matches(Tmp.PathTo("Elsewhere") + "\\*", shortProject)).IsFalse();
    }

    /// <summary>The spellings of one directory a Windows request can arrive in all match the entry naming
    /// it plainly; an extended spelling with no plain equivalent does not.</summary>
    [Test]
    [RunOn(OS.Windows)]
    [Arguments(@"\\?\", "", true)]
    [Arguments(@"\\.\", "", true)]
    [Arguments("", @"\", true)]
    [Arguments(@"\\?\", ".", false)]
    public async Task A_windows_spelling_of_an_allowed_directory_matches_its_plain_entry(string prefix, string suffix, bool expected) {
        string project = Tmp.CreateDir("allowed", "project");

        await Assert.That(RepoPathPattern.Matches(Tmp.PathTo("allowed") + "\\*", prefix + project + suffix)).IsEqualTo(expected);
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task Forward_slashes_and_drive_letter_case_reach_the_same_entry() {
        string project = Tmp.CreateDir("allowed", "project");
        var swapped = char.IsUpper(project[0]) ? char.ToLowerInvariant(project[0]) + project[1..] : char.ToUpperInvariant(project[0]) + project[1..];

        await Assert.That(RepoPathPattern.Matches(Tmp.PathTo("allowed") + "\\*", project.Replace('\\', '/'))).IsTrue();
        await Assert.That(RepoPathPattern.Matches(Tmp.PathTo("allowed").Replace('\\', '/') + "/*", project)).IsTrue();
        await Assert.That(RepoPathPattern.Matches(Tmp.PathTo("allowed") + "\\*", swapped)).IsTrue();
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task A_drive_root_glob_matches_everything_on_that_drive() {
        var project = Tmp.CreateDir("project");
        var drive   = Path.GetPathRoot(project.Path)!;

        await Assert.That(RepoPathPattern.Matches(drive + "*", project)).IsTrue();
        await Assert.That(RepoPathPattern.Matches(drive.TrimEnd('\\') + "/*", project)).IsTrue();
    }

    static string ShortNameOf(string path) {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /s /c \"for %I in (\"{path}\") do @echo %~sI\"") {
            RedirectStandardOutput = true, UseShellExecute = false,
        };
        using var cmd = System.Diagnostics.Process.Start(start)!;
        var output = cmd.StandardOutput.ReadToEnd().Trim();
        cmd.WaitForExit();

        return output;
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
