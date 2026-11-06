using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class WindowsPathSpellingTests {
    [Test]
    [Arguments(@"\\?\C:\repos\project", @"C:\repos\project")]
    [Arguments(@"\\?\UNC\server\share\repo", @"\\server\share\repo")]
    [Arguments(@"\\.\C:\repos\project", @"C:\repos\project")]
    [Arguments(@"\\.\UNC\server\share\repo", @"\\server\share\repo")]
    [Arguments(@"\\?\c:\repos", @"c:\repos")]
    [Arguments(@"C:/repos/project", @"C:\repos\project")]
    [Arguments(@"C:\repos\project", @"C:\repos\project")]
    [Arguments(@"\\server\share\repo", @"\\server\share\repo")]
    public async Task An_equivalent_spelling_folds_to_the_plain_one(string path, string expected) {
        await Assert.That(WindowsPathSpelling.Fold(path)).IsEqualTo(expected);
    }

    /// <summary>Each of these names something the plain spelling would trim or reinterpret, so folding
    /// it would let it match an entry for a different object.</summary>
    [Test]
    [Arguments(@"\\?\C:\repos\project.")]
    [Arguments(@"\\?\C:\repos\project ")]
    [Arguments(@"\\?\C:\repos\..\secret")]
    [Arguments(@"\\?\C:\repos\.\project")]
    [Arguments(@"\\?\C:\repos\CON")]
    [Arguments(@"\\?\C:\repos\nul.txt")]
    [Arguments(@"\\?\C:\repos\COM1")]
    [Arguments(@"\\?\UNC\server\share\repo.")]
    [Arguments(@"\\?\Volume{0b1d3c52-0000-0000-0000-100000000000}\repos")]
    [Arguments(@"\\?\GLOBALROOT\Device\HarddiskVolume1\repos")]
    public async Task A_spelling_with_no_plain_equivalent_keeps_its_prefix(string path) {
        await Assert.That(WindowsPathSpelling.Fold(path)).IsEqualTo(path);
    }

    [Test]
    [Arguments(@"\\?\C:\repos\COM10")]
    [Arguments(@"\\?\C:\repos\console")]
    public async Task A_name_that_only_starts_like_a_device_still_folds(string path) {
        await Assert.That(WindowsPathSpelling.Fold(path)).IsEqualTo(path[4..]);
    }
}
