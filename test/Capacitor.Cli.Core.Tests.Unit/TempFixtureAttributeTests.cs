namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Every injected fixture is a directory of its own: one shared between tests lets each see the
/// other's files, which no test that uses it would notice.</summary>
public class TempFixtureAttributeTests {
    [TempDir]           public required TempDir         Tmp     { get; init; }
    [TempDir]           public required TempDir         Second  { get; init; }
    [TempDaemonPaths]   public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task Two_properties_of_the_same_type_get_separate_directories() {
        await Assert.That(Second.Path).IsNotEqualTo(Tmp.Path);

        Tmp.CreateFile("only-in-first.txt");

        await Assert.That(File.Exists(Second.PathTo("only-in-first.txt"))).IsFalse();
    }

    [Test]
    public async Task Each_test_gets_its_own_directories() {
        // Two tests in one class: were the default lifetime ever widened, one would see the other's file.
        Tmp.CreateFile("marker-b.txt");
        Daemons.CreateFile("marker-b.txt");

        await Assert.That(Directory.GetFiles(Tmp.Path)).Count().IsEqualTo(1);
        await Assert.That(Directory.GetFiles(Daemons.Directory)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Each_test_gets_its_own_directories_again() {
        Tmp.CreateFile("marker-c.txt");
        Daemons.CreateFile("marker-c.txt");

        await Assert.That(Directory.GetFiles(Tmp.Path)).Count().IsEqualTo(1);
        await Assert.That(Directory.GetFiles(Daemons.Directory)).Count().IsEqualTo(1);
    }
}
