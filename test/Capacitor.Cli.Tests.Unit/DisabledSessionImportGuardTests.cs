using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>A detached session import stops once the person disables the session, and only then.</summary>
public class DisabledSessionImportGuardTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Session = "0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d";

    [Test]
    public async Task A_disable_landing_mid_import_stops_it_once() {
        var time  = new FakeTimeProvider();
        var stops = 0;
        using var guard = DisabledSessionImportGuard.Start(Session, Config.Root, time, () => Interlocked.Increment(ref stops));

        time.Advance(DisabledSessionImportGuard.PollInterval * 3);
        await Assert.That(stops).IsEqualTo(0);

        DisabledSessions.Mark(SessionIds.Canonical(Session)!, Config.Root);
        time.Advance(DisabledSessionImportGuard.PollInterval);
        time.Advance(DisabledSessionImportGuard.PollInterval * 3);

        await Assert.That(stops).IsEqualTo(1);
    }

    [Test]
    public async Task A_session_disabled_before_the_import_starts_stops_it_at_once() {
        DisabledSessions.Mark(SessionIds.Canonical(Session)!, Config.Root);
        var stops = 0;

        using var guard = DisabledSessionImportGuard.Start(Session, Config.Root, new FakeTimeProvider(), () => Interlocked.Increment(ref stops));

        await Assert.That(stops).IsEqualTo(1);
    }
}
