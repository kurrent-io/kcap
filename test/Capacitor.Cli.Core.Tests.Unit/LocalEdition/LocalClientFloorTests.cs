using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalClientFloorTests {
    [Test]
    [Arguments("1.3.1-alpha.0.4", true)]
    [Arguments("1.3.1", true)]
    [Arguments("1.4.0-beta.1", true)]
    [Arguments("2.0.0", true)]
    [Arguments("1.3.0", false)]
    [Arguments("1.3.0+abc123", false)]
    [Arguments("1.3.0-beta.10", false)]
    [Arguments("1.2.2", false)]
    [Arguments("0.12.0", false)]
    [Arguments("unknown", false)]
    [Arguments("garbage", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task Admits_every_build_after_the_newest_refused_and_nothing_it_cannot_read(string? version, bool admitted) =>
        await Assert.That(LocalClientFloor.Admits(version)).IsEqualTo(admitted);

    /// <summary>1.4.0 keeps to the local rules but has no local edition for Windows, so on Windows a kcap on PATH must be
    /// newer than it before a local installation is set up.</summary>
    [Test]
    [Arguments("1.4.0", false)]
    [Arguments("1.4.0+290d19ae", false)]
    [Arguments("1.4.0-beta.7", false)]
    [Arguments("1.3.1", false)]
    [Arguments("1.4.1-beta.2.12", true)]
    [Arguments("1.4.1", true)]
    public async Task On_windows_a_kcap_must_be_newer_than_the_last_release_with_no_windows_local_edition(string version, bool admitted) {
        await Assert.That(LocalClientFloor.AdmitsOn(windows: true, version)).IsEqualTo(admitted);
        await Assert.That(LocalClientFloor.NewestRefusedOn(windows: true)).IsEqualTo("1.4.0");
    }

    [Test]
    [Arguments("1.3.1", true)]
    [Arguments("1.4.0", true)]
    [Arguments("1.3.0", false)]
    public async Task Elsewhere_the_floor_is_the_local_rules_floor(string version, bool admitted) {
        await Assert.That(LocalClientFloor.AdmitsOn(windows: false, version)).IsEqualTo(admitted);
        await Assert.That(LocalClientFloor.NewestRefusedOn(windows: false)).IsEqualTo(LocalClientFloor.NewestRefused);
    }
}
