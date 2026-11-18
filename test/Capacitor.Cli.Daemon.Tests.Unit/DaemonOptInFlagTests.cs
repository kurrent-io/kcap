namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>The string parse behind the daemon's opt-in switches. The wiring and the one-time startup warning at the
/// <c>RunAsync</c> call site would need a full host boot.</summary>
public class DaemonOptInFlagTests {
    [Test]
    [Arguments("1")]
    [Arguments("true")]
    [Arguments("TRUE")]
    [Arguments("True")]
    [Arguments(" 1 ")]
    [Arguments("  true  ")]
    public async Task ParseOptInFlag_OnValues_ReturnsTrue(string input) {
        await Assert.That(DaemonRunner.ParseOptInFlag(input)).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("0")]
    [Arguments("false")]
    [Arguments("yes")]
    [Arguments("nonsense")]
    public async Task ParseOptInFlag_EverythingElse_ReturnsFalse(string? input) {
        await Assert.That(DaemonRunner.ParseOptInFlag(input)).IsFalse();
    }

    // KCAP_ACP_RECONNECT has the OPPOSITE default polarity: ON unless explicitly disabled.
    [Test]
    [Arguments("0")]
    [Arguments("false")]
    [Arguments("FALSE")]
    [Arguments(" false ")]
    public async Task ParseAcpReconnectFlag_OffValues_ReturnFalse(string input) {
        await Assert.That(DaemonRunner.ParseAcpReconnectFlag(input)).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("1")]
    [Arguments("true")]
    [Arguments("banana")]
    public async Task ParseAcpReconnectFlag_EverythingElse_ReturnsTrue(string? input) {
        await Assert.That(DaemonRunner.ParseAcpReconnectFlag(input)).IsTrue();
    }
}
