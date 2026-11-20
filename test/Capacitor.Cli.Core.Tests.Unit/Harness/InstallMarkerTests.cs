using System.Text;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

public class InstallMarkerTests {
    const string MarkerName = ".kcap-hooks-version";

    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>Pins the on-disk format every installed vendor already holds: the bare version, UTF-8 with no
    /// byte-order mark and no trailing newline, beside the config file.</summary>
    [Test]
    public async Task write_stores_the_bare_version_beside_the_config_file() {
        var config = Tmp.PathTo("vendor", "hooks.json");

        InstallMarker.Write(config, MarkerName);

        var bytes = await File.ReadAllBytesAsync(Tmp.PathTo("vendor", MarkerName));
        await Assert.That(bytes).IsEquivalentTo(Encoding.UTF8.GetBytes(CapacitorVersion.Current()));
        await Assert.That(File.Exists(config)).IsFalse();
    }

    [Test]
    public async Task read_trims_and_reports_a_missing_marker_as_null() {
        var config = Tmp.PathTo("vendor", "hooks.json");

        await Assert.That(InstallMarker.Read(config, MarkerName)).IsNull();
        await Assert.That(InstallMarker.Exists(config, MarkerName)).IsFalse();

        Tmp.CreateFile(["vendor", MarkerName], " 1.2.3\n");

        await Assert.That(InstallMarker.Read(config, MarkerName)).IsEqualTo("1.2.3");
        await Assert.That(InstallMarker.Exists(config, MarkerName)).IsTrue();
    }

    [Test]
    public async Task delete_removes_the_marker_and_tolerates_its_absence() {
        var config = Tmp.PathTo("vendor", "hooks.json");
        InstallMarker.Write(config, MarkerName);

        InstallMarker.Delete(config, MarkerName);
        InstallMarker.Delete(config, MarkerName);

        await Assert.That(File.Exists(Tmp.PathTo("vendor", MarkerName))).IsFalse();
    }

    [Test]
    public async Task a_path_without_a_directory_has_no_marker() {
        await Assert.That(InstallMarker.Read("hooks.json", MarkerName)).IsNull();
        await Assert.That(InstallMarker.Exists("hooks.json", MarkerName)).IsFalse();
    }
}
