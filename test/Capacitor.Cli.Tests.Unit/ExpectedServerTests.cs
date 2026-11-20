namespace Capacitor.Cli.Tests.Unit;

public class ExpectedServerTests {
    static Func<string, string?> Env(string? expected) => name => name == ExpectedServer.Var ? expected : null;

    [Test]
    [Arguments(null, "https://a.example.com")]
    [Arguments("", "https://a.example.com")]
    [Arguments("https://a.example.com", "https://A.example.com/")]
    [Arguments("https://a.example.com", "https://a.example.com:443")]
    public async Task Nothing_expected_or_the_same_server_lets_the_command_run(string? expected, string resolved) {
        await Assert.That(ExpectedServer.Mismatch(Env(expected), resolved)).IsNull();
    }

    /// The app's session id would otherwise reach whatever server the profile now names.
    [Test]
    [Arguments("https://a.example.com", "https://b.example.com", "kcap is pointed at https://b.example.com, not https://a.example.com. Nothing was changed.")]
    [Arguments("https://a.example.com", "", "kcap is pointed at no server, not https://a.example.com. Nothing was changed.")]
    public async Task Another_server_refuses_the_command(string expected, string resolved, string told) {
        await Assert.That(ExpectedServer.Mismatch(Env(expected), resolved)).IsEqualTo(told);
    }
}
