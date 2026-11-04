using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The ledger keeps every dispatch id the server may still resend: a settled one until the resend window has
/// passed since its last copy arrived, however many others came in between, and one in flight for as long as it is.
/// </summary>
public class DispatchedInputLedgerTests {
    readonly FakeTimeProvider      _time   = new();
    readonly DispatchedInputLedger _ledger;

    public DispatchedInputLedgerTests() => _ledger = new(_time);

    static DispatchState Again(DispatchedInputLedger ledger, Guid id) => ledger.Admit(id, out _);

    [Test]
    public async Task A_taken_id_outlives_any_number_of_later_dispatches_within_the_window() {
        var id = Guid.NewGuid();
        _ledger.Admit(id, out _);
        _ledger.MarkTaken(id);

        for (var i = 0; i < 10_000; i++) _ledger.Admit(Guid.NewGuid(), out _);
        _time.Advance(DispatchedInputLedger.Retention);

        await Assert.That(Again(_ledger, id)).IsEqualTo(DispatchState.Taken);
    }

    [Test]
    public async Task A_settled_id_expires_once_the_window_passes_without_a_resend() {
        var id = Guid.NewGuid();
        _ledger.Admit(id, out _);
        _ledger.MarkDropped(id, "unknown_agent");

        _time.Advance(DispatchedInputLedger.Retention + TimeSpan.FromSeconds(1));

        await Assert.That(Again(_ledger, id)).IsEqualTo(DispatchState.New);
    }

    [Test]
    public async Task A_resend_restarts_the_window() {
        var id = Guid.NewGuid();
        _ledger.Admit(id, out _);
        _ledger.MarkTaken(id);

        _time.Advance(DispatchedInputLedger.Retention - TimeSpan.FromMinutes(1));
        _ledger.Admit(id, out _);
        _time.Advance(DispatchedInputLedger.Retention - TimeSpan.FromMinutes(1));

        await Assert.That(Again(_ledger, id)).IsEqualTo(DispatchState.Taken);
    }

    [Test]
    public async Task An_id_in_flight_never_expires() {
        var id = Guid.NewGuid();
        _ledger.Admit(id, out _);

        _time.Advance(DispatchedInputLedger.Retention * 10);
        _ledger.Admit(Guid.NewGuid(), out _);

        await Assert.That(Again(_ledger, id)).IsEqualTo(DispatchState.InFlight);
    }
}
