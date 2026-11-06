using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The ledger keeps every dispatch id the server may still resend, settled or in flight, until the resend window
/// has passed since its last copy arrived, however many others came in between.
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

    /// <summary>An input acknowledged once taken can still be lost before it is written; the drop then wins.</summary>
    [Test]
    public async Task A_taken_id_dropped_later_is_answered_as_dropped_with_its_reason() {
        var id = Guid.NewGuid();
        _ledger.Admit(id, out _);
        _ledger.MarkTaken(id);
        _ledger.MarkDropped(id, "delivery_failed");

        var state = _ledger.Admit(id, out var reason);

        await Assert.That(state).IsEqualTo(DispatchState.Dropped);
        await Assert.That(reason).IsEqualTo("delivery_failed");
    }

    /// <summary>A repeat acknowledgement racing the report of a lost write must not undo the drop.</summary>
    [Test]
    public async Task A_dropped_id_stays_dropped_when_marked_taken_again() {
        var id = Guid.NewGuid();
        _ledger.Admit(id, out _);
        _ledger.MarkTaken(id);
        _ledger.MarkDropped(id, "delivery_failed");
        _ledger.MarkTaken(id);

        var state = _ledger.Admit(id, out var reason);

        await Assert.That(state).IsEqualTo(DispatchState.Dropped);
        await Assert.That(reason).IsEqualTo("delivery_failed");
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

    /// <summary>An id whose delivery never settles is kept through the resend window, then dropped like any other.</summary>
    [Test]
    public async Task An_id_in_flight_is_kept_through_the_window_then_dropped() {
        var id = Guid.NewGuid();
        _ledger.Admit(id, out _);

        _time.Advance(DispatchedInputLedger.Retention);
        await Assert.That(Again(_ledger, id)).IsEqualTo(DispatchState.InFlight);

        _time.Advance(DispatchedInputLedger.Retention + TimeSpan.FromSeconds(1));

        await Assert.That(Again(_ledger, id)).IsEqualTo(DispatchState.New);
    }
}
