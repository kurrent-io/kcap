using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// Where the join key is minted, and — because the bugs it guards are silent — that the
/// once-per-device <c>cli_first_run</c> event actually carries it.
///
/// <para>The key is minted as the facade starts, gated to the two interactive auth
/// commands (<c>setup</c>/<c>login</c>), BEFORE <c>NoticeAndFirstRun</c> captures <c>cli_first_run</c>.
/// Two placement bugs are cheap to reintroduce and produce no error at all. Mint AFTER
/// <c>cli_first_run</c> and that once-ever event ships without the key, with no later run able to
/// repair it. Mint UNCONDITIONALLY and every <c>recap</c>/<c>import</c> carries a key that correlates
/// nothing, redefining a per-auth-run id as a process id.</para>
///
/// <para>Pinned behaviourally, not by source shape: the behavioural tests capture
/// <c>cli_first_run</c> into the facade's own sink (no network, no disk), which is the only form
/// that proves the ordering rather than a proxy for it. <c>SetupJoinMintSourceGuardTests</c> in the
/// source-guards suite backs them up, because a mint added to a second file would slip past a test
/// that only starts a facade.</para>
/// </summary>
public class SetupJoinMintPlacementGuardTests {
    [TempDir] public required TempDir Tmp { get; init; }

    // The regression this whole feature's contract turns on: cli_first_run is captured once per
    // device, as the facade starts, and for an auth run it must carry the join key. Minting after
    // NoticeAndFirstRun leaves it without one, and being once-ever, no later run repairs it.
    [Test]
    [Arguments("setup")]
    [Arguments("login")]
    public async Task cli_first_run_carries_the_join_key_for_an_auth_command(string command) {
        var config = new ConfigRoot(Tmp.Path);
        var probe  = TelemetryProbe.Start(command, config);
        TelemetryTestGuards.AssertEnabled(command, config, probe.Telemetry);

        await Assert.That(probe.Telemetry.Join.Current).IsNotNull().Because("an auth command mints the key");

        var firstRun = probe.Events.SingleOrDefault(e => e.Name == "cli_first_run");
        await Assert.That(firstRun).IsNotNull()
            .Because("a fresh device's first auth command emits cli_first_run");
        await Assert.That(firstRun!.Properties[SetupJoin.PropertyName]?.GetValue<string>())
            .IsEqualTo(probe.Telemetry.Join.Current)
            .Because("cli_first_run must carry the minted key, which requires the mint to precede NoticeAndFirstRun");
    }

    // recap is reportable, so a fresh device still emits cli_first_run for it — but it has no auth run
    // to correlate, so it must mint nothing. An unconditional mint as the facade starts is exactly what
    // this catches: it would set Current and stamp join_id onto recap/import events.
    [Test]
    public async Task A_non_auth_command_mints_no_join_key() {
        var config = new ConfigRoot(Tmp.Path);
        var probe  = TelemetryProbe.Start("recap", config);
        TelemetryTestGuards.AssertEnabled("recap", config, probe.Telemetry);

        await Assert.That(probe.Telemetry.Join.Current).IsNull().Because("a non-auth command must not mint the key");

        var firstRun = probe.Events.SingleOrDefault(e => e.Name == "cli_first_run");
        await Assert.That(firstRun).IsNotNull().Because("recap is reportable, so a fresh device still emits cli_first_run");
        await Assert.That(firstRun!.Properties.ContainsKey(SetupJoin.PropertyName)).IsFalse()
            .Because("no auth run means no join_id on the event");
    }

}
