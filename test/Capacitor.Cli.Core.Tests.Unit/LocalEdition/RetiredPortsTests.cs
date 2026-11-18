using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

/// <summary>The user-wide record of retired ports outlives any one root, so it only ever grows, and a record that cannot
/// be read is never taken as empty.</summary>
public class RetiredPortsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    UserHome Home => field ??= new(Tmp.CreateDir("home").Path);

    static readonly RetiredInstallation First  = new("inst-1", 9473, "http://localhost:9473");
    static readonly RetiredInstallation Second = new("inst-2", 51234, "http://localhost:51234");

    [Test]
    public async Task It_lives_under_the_home_not_a_local_root() =>
        await Assert.That(RetiredPorts.PathFor(Home)).IsEqualTo(Path.Combine(Home.Path, ".config", "kcap", "local-retired.json"));

    [Test]
    public async Task A_missing_record_reads_empty() {
        var read = RetiredPorts.Read(Home);

        await Assert.That(read is StateRead<InstallRegistry>.Present { Value.Retired.Count: 0 }).IsTrue();
    }

    [Test]
    public async Task Appending_keeps_earlier_entries_and_records_each_once() {
        RetiredPorts.Append(Home, First);
        RetiredPorts.Append(Home, Second);
        RetiredPorts.Append(Home, First);

        var registry = ((StateRead<InstallRegistry>.Present)RetiredPorts.Read(Home)).Value;

        await Assert.That(registry.Retired).IsEquivalentTo([First, Second]);
        await Assert.That(registry.Active).IsNull();
        await Assert.That(registry.IsRetiredPort(9473)).IsTrue();
    }

    [Test]
    public async Task A_corrupt_record_reads_unreadable_and_is_not_overwritten() {
        var path = Tmp.CreateFile(["home", ".config", "kcap", "local-retired.json"], "{not json");

        await Assert.That(RetiredPorts.Read(Home) is StateRead<InstallRegistry>.Unreadable(var unreadable, _) && unreadable == path).IsTrue();
        await Assert.That(() => RetiredPorts.Append(Home, First)).Throws<LocalLifecycleException>();
        await Assert.That(File.ReadAllText(path)).IsEqualTo("{not json");
    }
}
