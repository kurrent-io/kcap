using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

/// <summary>kcap's plugin exports one entry point for v1 and v2, a form OpenCode v1 supports only from
/// 1.18.29; an older v1 is told to upgrade, and anything kcap cannot read is left alone.</summary>
public class OpenCodeVersionFloorTests {
    [Test]
    [Arguments("1.18.28")]
    [Arguments("1.17.9")]
    [Arguments("0.9.0")]
    public async Task A_v1_older_than_the_floor_is_told_to_upgrade(string installed) {
        var notice = OpenCodeVersionFloor.UpgradeNotice(installed);

        await Assert.That(notice).IsNotNull();
        await Assert.That(notice!).Contains(installed);
        await Assert.That(notice!).Contains("1.18.29");
        await Assert.That(notice!).Contains("opencode upgrade");
    }

    [Test]
    [Arguments("1.18.29")]
    [Arguments("1.19.5")]
    [Arguments("2.3.0")]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("dev")]
    public async Task A_supported_or_unreadable_version_gets_no_notice(string? installed) =>
        await Assert.That(OpenCodeVersionFloor.UpgradeNotice(installed)).IsNull();
}
