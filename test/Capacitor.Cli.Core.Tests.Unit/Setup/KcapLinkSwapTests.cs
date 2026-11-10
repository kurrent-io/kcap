using System.Runtime.Versioning;
using Capacitor.Cli.Core.Setup;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

[ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
public class KcapLinkSwapTests {
    [TempDir] public required TempDir Tmp { get; init; }

    (string Folder, string Link) Linked(string target, string folder = "bin") {
        var dir  = Tmp.CreateDir(folder).Path;
        var link = Path.Combine(dir, "kcap");
        File.CreateSymbolicLink(link, target);
        return (dir, link);
    }

    [Test]
    public async Task A_link_still_naming_the_expected_target_is_replaced_and_nothing_is_left_beside_it() {
        var (folder, link) = Linked("/old/kcap");

        KcapLinkSwap.Replace(link, "/old/kcap", "/new/kcap");

        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo("/new/kcap");
        await Assert.That(Directory.GetFileSystemEntries(folder)).IsEquivalentTo([link]);
    }

    [Test]
    public async Task A_link_that_names_another_target_is_left_and_the_swap_throws() {
        var (folder, link) = Linked("/other/kcap");

        await Assert.That(() => KcapLinkSwap.Replace(link, "/old/kcap", "/new/kcap")).Throws<IOException>();

        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo("/other/kcap");
        await Assert.That(Directory.GetFileSystemEntries(folder)).IsEquivalentTo([link]);
    }

    [Test]
    public async Task A_regular_file_is_never_taken_for_the_expected_link() {
        var file = Tmp.CreateFile("bin/kcap", "binary");

        await Assert.That(() => KcapLinkSwap.Replace(file, "/old/kcap", "/new/kcap")).Throws<IOException>();

        await Assert.That(File.ReadAllText(file)).IsEqualTo("binary");
    }

    [Test]
    public async Task A_folder_with_spaces_swaps_like_any_other() {
        var (folder, link) = Linked("/old dir/kcap", "my home/.nvm/versions/node/v20/bin");

        KcapLinkSwap.Replace(link, "/old dir/kcap", "/Applications/Kcap Beta.app/Contents/MacOS/kcap");

        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo("/Applications/Kcap Beta.app/Contents/MacOS/kcap");
        await Assert.That(Directory.GetFileSystemEntries(folder)).IsEquivalentTo([link]);
    }

    /// <summary>The caller tells a folder it cannot write apart from a changed link: one needs an administrator, the
    /// other is someone else's.</summary>
    [Test]
    public async Task A_folder_this_user_cannot_write_lets_the_denial_through() {
        var (folder, link) = Linked("/old/kcap");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            await Assert.That(() => KcapLinkSwap.Replace(link, "/old/kcap", "/new/kcap")).Throws<UnauthorizedAccessException>();
            await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo("/old/kcap");
        } finally {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task An_entry_its_check_still_finds_unchanged_is_replaced_even_when_it_is_no_link() {
        var file    = Tmp.CreateFile("bin/kcap", "binary");
        var checks  = 0;

        var replaced = await KcapLinkSwap.ReplaceAsync(file, "/new/kcap", _ => {
            checks++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        await Assert.That(replaced).IsTrue();
        await Assert.That(checks).IsEqualTo(1);
        await Assert.That(new FileInfo(file).LinkTarget).IsEqualTo("/new/kcap");
        await Assert.That(Directory.GetFileSystemEntries(Tmp.PathTo("bin"))).IsEquivalentTo([file]);
    }

    [Test]
    public async Task An_entry_its_check_finds_changed_is_left_with_nothing_beside_it() {
        var file = Tmp.CreateFile("bin/kcap", "binary");

        var replaced = await KcapLinkSwap.ReplaceAsync(file, "/new/kcap", _ => Task.FromResult(false), CancellationToken.None);

        await Assert.That(replaced).IsFalse();
        await Assert.That(File.ReadAllText(file)).IsEqualTo("binary");
        await Assert.That(Directory.GetFileSystemEntries(Tmp.PathTo("bin"))).IsEquivalentTo([file]);
    }
}
