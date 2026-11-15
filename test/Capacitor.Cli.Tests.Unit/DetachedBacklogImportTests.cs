namespace Capacitor.Cli.Tests.Unit;

public class DetachedBacklogImportTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>The continuation runs with no terminal and no hook to answer it, so it must name its vendor and session
    /// (which stands in for a scope and a confirmation) and read the hook's own config root, or it imports nothing.</summary>
    [Test]
    public async Task The_import_names_its_vendor_and_session_and_reads_the_hooks_config() {
        var starter = FakeProcessStarter.Refusing();

        var handed = DetachedBacklogImport.Spawn("--pi", "s-1", Config.Root, starter);

        await Assert.That(handed).IsFalse();
        await Assert.That(starter.Seen!.ArgumentList).IsEquivalentTo(
            ["import", "--pi", "--session", "s-1", "--skip-title", "--no-update-check"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(starter.Seen.Environment[Capacitor.Cli.Core.ConfigRoot.ConfigDirEnvVar]).IsEqualTo(Config.Root.Directory);
    }
}
