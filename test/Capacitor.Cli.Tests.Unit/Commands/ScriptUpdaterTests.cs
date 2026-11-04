using TUnit.Core.Enums;
using System.Runtime.Versioning;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A script install updates by verifying the archive against the release manifest, extracting it into a
/// new version directory and switching <c>current</c>. Every verification failure installs nothing:
/// <c>current</c> keeps its target and no version directory appears.
/// </summary>
public class ScriptUpdaterTests {
    static bool Unsupported => OperatingSystem.IsWindows();

    static async Task<(bool Ok, string Out, string Err)> Install(
            ScriptReleaseFixture f, string version, string channel = "latest", TimeSpan? downloadTimeout = null,
            Func<string, bool>? inUse = null) {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var ok = await new ScriptUpdater(f.Client(downloadTimeout)) { InUse = inUse ?? ScriptUpdater.AnyFileInUse }
            .InstallAsync(f.Layout, version, channel, ScriptReleaseFixture.Rid, stdout, stderr, CancellationToken.None);
        return (ok, stdout.ToString(), stderr.ToString());
    }

    static async Task AssertNothingInstalled(ScriptReleaseFixture f, string version) {
        await Assert.That(f.CurrentTarget).IsEqualTo("versions/1.0.0");
        await Assert.That(Directory.Exists(f.Layout.VersionDir(version))).IsFalse();
        await Assert.That(Directory.EnumerateDirectories(f.Layout.Versions).Select(p => Path.GetFileName(p)!).ToArray()).IsEquivalentTo(["1.0.0"]);
    }

    [Test]
    public async Task A_verified_release_lands_in_its_own_directory_and_becomes_current() {
        Skip.When(Unsupported, "the switch is a symlink rename; Windows uses a junction");
        using var f = new ScriptReleaseFixture();
        f.Publish("1.5.0");

        var (ok, _, err) = await Install(f, "1.5.0", channel: "beta");

        await Assert.That(ok).IsTrue();
        await Assert.That(err).IsEmpty();
        await Assert.That(f.CurrentTarget).IsEqualTo("versions/1.5.0");
        await Assert.That(File.ReadAllText(f.Layout.CurrentBin("kcap"))).IsEqualTo("kcap 1.5.0");
        await Assert.That(File.Exists(Path.Combine(f.Layout.Current, "kcap", ".mcp.json"))).IsTrue();
        await Assert.That(File.ReadAllText(f.Layout.Marker)).Contains("\"channel\":\"beta\"");
        // The old version stays: a running daemon or agent may still be executing it.
        await Assert.That(File.ReadAllText(Path.Combine(f.Layout.VersionDir("1.0.0"), "bin", "kcap"))).IsEqualTo("old");
    }

    /// <summary>The bytes are checked against the manifest from the other host; a swapped binary fails.</summary>
    [Test]
    public async Task A_checksum_mismatch_installs_nothing() {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("1.5.0", manifestSha: new string('a', 64));

        var (ok, _, err) = await Install(f, "1.5.0");

        await Assert.That(ok).IsFalse();
        await Assert.That(err).Contains("Checksum mismatch");
        await Assert.That(err).Contains("Nothing was installed");
        await AssertNothingInstalled(f, "1.5.0");
    }

    [Test]
    public async Task A_malformed_checksum_installs_nothing_and_downloads_nothing() {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("1.5.0", manifestSha: "not-a-digest");

        var (ok, _, err) = await Install(f, "1.5.0");

        await Assert.That(ok).IsFalse();
        await Assert.That(err).Contains("invalid checksum");
        await Assert.That(f.Requests.Any(p => p.EndsWith(".tar.gz", StringComparison.Ordinal))).IsFalse();
        await AssertNothingInstalled(f, "1.5.0");
    }

    [Test]
    public async Task A_release_without_this_rid_installs_nothing() {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("1.5.0", archiveRid: "osx-arm64");

        var (ok, _, err) = await Install(f, "1.5.0");

        await Assert.That(ok).IsFalse();
        await Assert.That(err).Contains($"no build for {ScriptReleaseFixture.Rid}");
        await AssertNothingInstalled(f, "1.5.0");
    }

    [Test]
    public async Task An_unreachable_manifest_installs_nothing() {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();

        var (ok, _, err) = await Install(f, "1.5.0");

        await Assert.That(ok).IsFalse();
        await Assert.That(err).Contains("Could not read the manifest");
        await AssertNothingInstalled(f, "1.5.0");
    }

    [Test]
    [Arguments("../1.5.0")]
    [Arguments("latest")]
    public async Task A_version_that_is_not_a_release_is_refused(string version) {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();

        var (ok, _, err) = await Install(f, version);

        await Assert.That(ok).IsFalse();
        await Assert.That(err).Contains("not a release version");
        await Assert.That(f.Requests).IsEmpty();
    }

    /// <summary>A real directory at <c>current</c> was not made by the installer, so it is never replaced.</summary>
    [Test]
    public async Task A_current_that_is_not_a_link_is_refused() {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("1.5.0");
        f.MakeCurrentADirectory();

        var (ok, _, err) = await Install(f, "1.5.0");

        await Assert.That(ok).IsFalse();
        await Assert.That(err).Contains("is not a link the installer made");
        await Assert.That(Directory.Exists(f.Layout.VersionDir("1.5.0"))).IsFalse();
    }

    /// <summary>The active version is never replaced: everything that runs kcap runs it from there.</summary>
    [Test]
    public async Task The_active_version_is_left_in_place_and_nothing_is_downloaded() {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("1.0.0");

        var (ok, output, _) = await Install(f, "1.0.0", channel: "beta");

        await Assert.That(ok).IsTrue();
        await Assert.That(output).Contains("already the active version");
        await Assert.That(File.ReadAllText(f.Layout.CurrentBin("kcap"))).IsEqualTo("old");
        await Assert.That(f.Requests.Any(p => p.EndsWith(".tar.gz", StringComparison.Ordinal))).IsFalse();
        await Assert.That(File.ReadAllText(f.Layout.Marker)).Contains("\"channel\":\"beta\"");
    }

    /// <summary>A process started from a version before <c>current</c> moved away may still run there, so
    /// installing that version again moves the old directory aside instead of deleting it.</summary>
    [Test]
    public async Task A_leftover_version_directory_in_use_is_moved_aside_not_deleted() {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.LeaveVersion("1.5.0", "leftover");
        f.Publish("1.5.0");

        var (ok, _, _) = await Install(f, "1.5.0", inUse: dir => Path.GetFileName(dir).StartsWith(".replaced-", StringComparison.Ordinal));

        await Assert.That(ok).IsTrue();
        await Assert.That(File.ReadAllText(f.Layout.CurrentBin("kcap"))).IsEqualTo("kcap 1.5.0");
        var aside = Directory.EnumerateDirectories(f.Layout.Versions, ".replaced-1.5.0-*").Single();
        await Assert.That(File.ReadAllText(Path.Combine(aside, "bin", "kcap"))).IsEqualTo("leftover");
    }

    [Test]
    public async Task A_stalled_download_fails_within_its_deadline_and_installs_nothing() {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("1.5.0", archiveDelay: TimeSpan.FromSeconds(10));

        var (ok, _, err) = await Install(f, "1.5.0", downloadTimeout: TimeSpan.FromMilliseconds(300));

        await Assert.That(ok).IsFalse();
        await Assert.That(err).Contains("Could not download");
        await AssertNothingInstalled(f, "1.5.0");
    }

    /// <summary>The marker is rewritten after the switch; failing to write it does not undo an update.</summary>
    [Test]
    public async Task A_marker_that_cannot_be_written_still_reports_the_update() {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.Publish("1.5.0");
        f.BlockMarker();

        var (ok, _, err) = await Install(f, "1.5.0");

        await Assert.That(ok).IsTrue();
        await Assert.That(f.CurrentTarget).IsEqualTo("versions/1.5.0");
        await Assert.That(err).Contains("Could not record the update channel");
    }

    [Test]
    public async Task Switching_replaces_the_link_and_leaves_no_temporary_one() {
        Skip.When(Unsupported, "rename(2) is POSIX");
        using var tmp = new TempDir();
        tmp.CreateDir("versions", "a");
        tmp.CreateDir("versions", "b");
        var link = tmp.PathTo("current");
        Directory.CreateSymbolicLink(link, "versions/a");

        ScriptUpdater.SwitchLink(link, "versions/b");

        await Assert.That(new DirectoryInfo(link).LinkTarget).IsEqualTo("versions/b");
        await Assert.That(Directory.EnumerateFileSystemEntries(tmp.Path).Select(p => Path.GetFileName(p)!).ToArray())
            .IsEquivalentTo(["versions", "current"]);
    }

    /// <summary>An update keeps the version it replaced, which a daemon started before the switch still runs,
    /// and deletes the older ones.</summary>
    [Test]
    public async Task An_update_prunes_versions_older_than_the_one_it_replaced() {
        Skip.When(Unsupported, "the fixture's current is a symlink");
        using var f = new ScriptReleaseFixture();
        f.LeaveVersion("0.9.0", "older");
        f.Publish("1.5.0");

        var (ok, output, _) = await Install(f, "1.5.0");

        await Assert.That(ok).IsTrue();
        await Assert.That(output).Contains("Removed 1 unused kcap version.");
        await Assert.That(Directory.EnumerateDirectories(f.Layout.Versions).Select(p => Path.GetFileName(p)!).ToArray())
            .IsEquivalentTo(["1.0.0", "1.5.0"]);
    }

    [Test]
    public async Task Pruning_keeps_what_it_is_told_to_and_what_is_in_use() {
        using var tmp = new TempDir();
        var layout = new ScriptInstallLayout(tmp.Path);
        foreach (var name in new[] { "1.0.0", "1.1.0", "1.2.0", "1.3.0-beta.1", ".replaced-1.2.0-abc", ".update-12-def", ".staging-34", "notes" })
            tmp.CreateFile($"versions/{name}/bin/kcap");
        var unrelated = tmp.CreateFile("elsewhere/bin/kcap");
        DirectoryLink.Create(layout.VersionDir("0.9.0"), tmp.PathTo("elsewhere"));
        var updater = new ScriptUpdater(null!) { InUse = dir => Path.GetFileName(dir) == "1.1.0" };

        var pruned = updater.PruneVersions(layout, keep: [layout.VersionDir("1.3.0-beta.1"), layout.VersionDir("1.2.0"), null], TextWriter.Null);

        await Assert.That(pruned).IsEqualTo(2);
        await Assert.That(Directory.EnumerateDirectories(layout.Versions).Select(p => Path.GetFileName(p)!).ToArray())
            .IsEquivalentTo(["0.9.0", "1.1.0", "1.2.0", "1.3.0-beta.1", ".update-12-def", ".staging-34", "notes"]);
        await Assert.That(File.Exists(unrelated)).IsTrue();
    }

    /// <summary>A <c>versions</c> that is a link reaches a directory kcap does not own, so nothing behind it is
    /// pruned, even entries that carry a kcap binary. On Windows the link is a junction.</summary>
    [Test]
    public async Task Pruning_through_a_linked_versions_directory_deletes_nothing() {
        using var tmp = new TempDir();
        var layout = new ScriptInstallLayout(tmp.CreateDir("root"));
        var unrelated = new[] { "0.9.0", "1.0.0", ".replaced-1.0.0-abc", ".pruning-1.0.0-abc" }.Select(name => tmp.CreateFile($"shared/{name}/bin/kcap")).ToArray();
        DirectoryLink.Create(layout.Versions, tmp.PathTo("shared"));
        var updater = new ScriptUpdater(null!) { InUse = _ => false };

        var pruned = updater.PruneVersions(layout, keep: [layout.VersionDir("1.1.0")], TextWriter.Null);

        await Assert.That(pruned).IsEqualTo(0);
        foreach (var file in unrelated) await Assert.That(File.Exists(file)).IsTrue();
    }

    /// <summary>An install root reached through a link is still kcap's, but only a version directory holding
    /// kcap's binary is provably an installed version: a version-like name alone is not.</summary>
    [Test]
    public async Task Pruning_through_a_linked_install_root_deletes_only_installed_versions() {
        using var tmp = new TempDir();
        var unrelated = new[] { "0.9.0", "1.0.0", ".replaced-1.0.0-abc" }.Select(name => tmp.CreateFile($"elsewhere/versions/{name}/data")).ToArray();
        tmp.CreateFile("elsewhere/versions/0.8.0/bin/kcap");
        DirectoryLink.Create(tmp.PathTo("root"), tmp.PathTo("elsewhere"));
        var layout  = new ScriptInstallLayout(tmp.PathTo("root"));
        var updater = new ScriptUpdater(null!) { InUse = _ => false };

        var pruned = updater.PruneVersions(layout, keep: [layout.VersionDir("1.1.0")], TextWriter.Null);

        await Assert.That(pruned).IsEqualTo(1);
        await Assert.That(Directory.Exists(tmp.PathTo("elsewhere/versions/0.8.0"))).IsFalse();
        foreach (var file in unrelated) await Assert.That(File.Exists(file)).IsTrue();
    }

    /// <summary>A path checked once is not bound to the delete: whichever component is swapped for a link between
    /// the listing and the delete, what the link reaches is left alone, even when it carries a kcap binary.</summary>
    [Test]
    [Arguments("root")]
    [Arguments("root/versions")]
    [Arguments("root/versions/0.9.0")]
    public async Task A_component_swapped_for_a_link_before_the_delete_keeps_what_it_reaches(string swapped) {
        using var tmp = new TempDir();
        tmp.CreateFile("root/versions/0.9.0/bin/kcap");
        tmp.CreateFile("root/versions/1.0.0/bin/kcap");
        var foreign = tmp.CreateFile("foreign/root/versions/0.9.0/bin/kcap");
        var layout  = new ScriptInstallLayout(tmp.PathTo("root"));
        var updater = new ScriptUpdater(null!) {
            InUse = _ => {
                Directory.Move(tmp.PathTo(swapped), tmp.PathTo("aside"));
                DirectoryLink.Create(tmp.PathTo(swapped), tmp.PathTo("foreign", swapped));
                return false;
            },
        };

        var pruned = updater.PruneVersions(layout, keep: [layout.VersionDir("1.0.0")], TextWriter.Null);

        await Assert.That(pruned).IsEqualTo(0);
        await Assert.That(File.Exists(foreign)).IsTrue();
    }

    /// <summary>A link inside a version directory goes before the directory does, so the delete neither follows it
    /// nor, on Windows where it is a junction, stops partway at it.</summary>
    [Test]
    public async Task A_version_holding_a_link_is_pruned_without_following_it() {
        using var tmp = new TempDir();
        tmp.CreateFile("root/versions/0.9.0/bin/kcap");
        var outside = tmp.CreateFile("outside/keep");
        DirectoryLink.Create(tmp.PathTo("root/versions/0.9.0/share"), tmp.PathTo("outside"));
        var layout  = new ScriptInstallLayout(tmp.PathTo("root"));
        var updater = new ScriptUpdater(null!) { InUse = _ => false };

        var pruned = updater.PruneVersions(layout, keep: [], TextWriter.Null);

        await Assert.That(pruned).IsEqualTo(1);
        await Assert.That(Directory.Exists(layout.VersionDir("0.9.0"))).IsFalse();
        await Assert.That(File.Exists(outside)).IsTrue();
    }

    /// <summary>A version whose directory refuses the delete partway (here, once its binary is gone) is still
    /// recognised as kcap's by the next prune, which finishes it; the failure is reported, naming the version.</summary>
    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task A_version_that_fails_to_delete_partway_is_reported_and_removed_by_the_next_run() {
        using var tmp = new TempDir();
        tmp.CreateFile("root/versions/0.9.0/bin/kcap");
        var layout  = new ScriptInstallLayout(tmp.PathTo("root"));
        var updater = new ScriptUpdater(null!) { InUse = _ => false };
        var stderr  = new StringWriter();

        int first;
        try {
            File.SetUnixFileMode(layout.VersionDir("0.9.0"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
            first = updater.PruneVersions(layout, keep: [], stderr);
        } finally {
            foreach (var dir in Directory.EnumerateDirectories(layout.Versions))
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Assert.That(first).IsEqualTo(0);
        await Assert.That(stderr.ToString()).Contains("0.9.0").And.Contains("next update");

        var second = updater.PruneVersions(layout, keep: [], TextWriter.Null);

        await Assert.That(second).IsEqualTo(1);
        await Assert.That(Directory.EnumerateFileSystemEntries(layout.Versions).ToArray()).IsEmpty();
    }

    /// <summary>A <c>.pruning-</c> directory is a version kcap began deleting; only kcap makes one, so it needs no
    /// binary to be removed.</summary>
    [Test]
    public async Task A_leftover_pruning_directory_is_removed() {
        using var tmp = new TempDir();
        tmp.CreateFile($"root/versions/.pruning-0.9.0-{Guid.NewGuid():N}/lib/file");
        var layout  = new ScriptInstallLayout(tmp.PathTo("root"));
        var updater = new ScriptUpdater(null!) { InUse = _ => false };

        var pruned = updater.PruneVersions(layout, keep: [], TextWriter.Null);

        await Assert.That(pruned).IsEqualTo(1);
        await Assert.That(Directory.EnumerateFileSystemEntries(layout.Versions).ToArray()).IsEmpty();
    }

    /// <summary>Only the exact name the updater gives a version it is deleting marks a tombstone; a directory that
    /// merely starts with <c>.pruning-</c> is someone else's.</summary>
    [Test]
    public async Task A_directory_named_like_a_tombstone_but_not_made_by_kcap_is_kept() {
        using var tmp = new TempDir();
        string[] names = [".pruning-backups", ".pruning-0.9.0-abc", $".pruning-notes-{Guid.NewGuid():N}", $".pruning-0.9.0-{Guid.NewGuid().ToString("N").ToUpperInvariant()}"];
        var files   = names.Select(name => tmp.CreateFile($"root/versions/{name}/data")).ToArray();
        var layout  = new ScriptInstallLayout(tmp.PathTo("root"));
        var updater = new ScriptUpdater(null!) { InUse = _ => false };

        var pruned = updater.PruneVersions(layout, keep: [], TextWriter.Null);

        await Assert.That(pruned).IsEqualTo(0);
        foreach (var file in files) await Assert.That(File.Exists(file)).IsTrue();
    }

    /// <summary>A <c>.pruning-</c> name proves nothing about where a link goes, so a linked one is left as it is.
    /// On Windows the link is a junction.</summary>
    [Test]
    public async Task A_linked_pruning_entry_is_left_alone() {
        using var tmp = new TempDir();
        tmp.CreateDir("root/versions");
        var outside = tmp.CreateFile("outside/lib/file");
        var link    = tmp.PathTo($"root/versions/.pruning-0.9.0-{Guid.NewGuid():N}");
        DirectoryLink.Create(link, tmp.PathTo("outside"));
        var layout  = new ScriptInstallLayout(tmp.PathTo("root"));
        var updater = new ScriptUpdater(null!) { InUse = _ => false };

        var pruned = updater.PruneVersions(layout, keep: [], TextWriter.Null);

        await Assert.That(pruned).IsEqualTo(0);
        await Assert.That(new DirectoryInfo(link).LinkTarget).IsNotNull();
        await Assert.That(File.Exists(outside)).IsTrue();
    }

    /// <summary>Only the new version and the one it replaced are kept, so a rollback deletes an unused
    /// newer version too.</summary>
    [Test]
    public async Task Pruning_after_a_rollback_deletes_an_unused_newer_version() {
        using var tmp = new TempDir();
        var layout = new ScriptInstallLayout(tmp.Path);
        foreach (var name in new[] { "1.0.0", "1.1.0", "2.0.0" })
            tmp.CreateFile($"versions/{name}/bin/kcap");
        var updater = new ScriptUpdater(null!) { InUse = _ => false };

        var pruned = updater.PruneVersions(layout, keep: [layout.VersionDir("1.0.0"), layout.VersionDir("1.1.0")], TextWriter.Null);

        await Assert.That(pruned).IsEqualTo(1);
        await Assert.That(Directory.EnumerateDirectories(layout.Versions).Select(p => Path.GetFileName(p)!).ToArray())
            .IsEquivalentTo(["1.0.0", "1.1.0"]);
    }

    /// <summary>A file someone holds open marks its directory in use; a running binary refuses the same
    /// exclusive write open on Windows and Linux.</summary>
    [Test]
    public async Task A_directory_with_a_file_held_open_is_in_use() {
        using var tmp = new TempDir();
        var dir = tmp.CreateDir("1.0.0");
        var exe = tmp.CreateFile("1.0.0/bin/kcap");

        await Assert.That(ScriptUpdater.AnyFileInUse(dir)).IsFalse();
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.That(ScriptUpdater.AnyFileInUse(dir)).IsTrue();
        await Assert.That(ScriptUpdater.AnyFileInUse(dir)).IsFalse();
    }

    [Test]
    public async Task A_link_to_a_file_held_open_elsewhere_does_not_mark_the_directory_in_use() {
        Skip.When(Unsupported, "a file symlink needs a privilege on Windows");
        using var tmp = new TempDir();
        var dir     = tmp.CreateDir("1.0.0");
        var outside = tmp.CreateFile("elsewhere/held");
        File.CreateSymbolicLink(tmp.PathTo("1.0.0/held"), outside);

        using (new FileStream(outside, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.That(ScriptUpdater.AnyFileInUse(dir)).IsFalse();
    }
}
