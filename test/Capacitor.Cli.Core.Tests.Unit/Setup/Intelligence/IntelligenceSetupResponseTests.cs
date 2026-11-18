using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Core.Tests.Unit.Setup.Intelligence;

/// <summary>The days an import's background work takes round up, and an allowance the server cannot have meant gives
/// no estimate</summary>
public class IntelligenceSetupResponseTests {
    [Test]
    [Arguments(200, 1, 1)]
    [Arguments(200, 200, 1)]
    [Arguments(200, 201, 2)]
    [Arguments(200, 0, 0)]
    [Arguments(0, 10, null)]
    public async Task Days_round_up_at_the_daily_allowance(int perDay, int sessions, int? days) =>
        await Assert.That(new IntelligenceSetupResponse("none", null, null, false, perDay, []).DaysFor(sessions)).IsEqualTo(days);

    [Test]
    [Arguments(300, 5, "under a day")]
    [Arguments(300, 300, "about a day")]
    [Arguments(300, 301, "about 2 days")]
    [Arguments(300, 0, null)]
    [Arguments(0, 10, null)]
    public async Task Work_inside_one_days_allowance_reads_under_a_day(int perDay, int sessions, string? duration) =>
        await Assert.That(new IntelligenceSetupResponse("none", null, null, false, perDay, []).DurationFor(sessions)).IsEqualTo(duration);
}
