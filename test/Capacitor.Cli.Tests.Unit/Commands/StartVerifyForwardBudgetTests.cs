using System.Globalization;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Pins that the <c>start --verify</c> window override can only shorten the window: anything else keeps
/// the default.</summary>
public class StartVerifyForwardBudgetTests {
    [Test]
    public async Task A_shorter_value_replaces_the_default() =>
        await Assert.That(DaemonServiceCommands.ShortenedStartForwardBudget("1500")).IsEqualTo(TimeSpan.FromMilliseconds(1500));

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("0")]
    [Arguments("-5")]
    [Arguments("1.5")]
    [Arguments(" 100")]
    [Arguments("abc")]
    [Arguments("99999999999")]
    public async Task Anything_but_a_positive_whole_number_keeps_the_default(string? value) =>
        await Assert.That(DaemonServiceCommands.ShortenedStartForwardBudget(value)).IsNull();

    [Test]
    public async Task The_default_or_longer_keeps_the_default() {
        var atDefault = ((int)ServiceVerify.DefaultForwardBudget.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        await Assert.That(DaemonServiceCommands.ShortenedStartForwardBudget(atDefault)).IsNull();
        await Assert.That(DaemonServiceCommands.ShortenedStartForwardBudget("600000")).IsNull();
    }
}
