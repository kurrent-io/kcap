using System.Collections.Frozen;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// The sessions whose hand-off card the user closed, kept in app state so the card stays closed
/// after a restart. A dismissal the store fails to write still holds for this run.
public sealed class HandoffOutcomeDismissals(IAppStateStore state) {
    /// The daemon keeps this many outcomes, so a session older than that has no card left to close.
    public const int Capacity = 200;

    readonly Lock _lock = new();
    readonly HashSet<string> _thisRun = new(StringComparer.Ordinal);
    readonly Subject<string> _dismissed = new();
    Task<IReadOnlySet<string>>? _stored;

    /// Each dismissed session, by its canonical id.
    public IObservable<string> Dismissed => _dismissed;

    public async Task<bool> IsDismissedAsync(string sessionId) {
        if (SessionIds.Canonical(sessionId) is not { } key) return false;

        Task<IReadOnlySet<string>> stored;
        lock (_lock) {
            if (_thisRun.Contains(key)) return true;
            stored = _stored ??= LoadAsync();
        }
        if ((await stored.ConfigureAwait(false)).Contains(key)) return true;
        lock (_lock) return _thisRun.Contains(key);
    }

    public async Task DismissAsync(string sessionId) {
        if (SessionIds.Canonical(sessionId) is not { } key) return;

        lock (_lock) _thisRun.Add(key);
        _dismissed.OnNext(key);
        await state.UpdateAsync(s => s with {
            DismissedHandoffOutcomes = [.. (s.DismissedHandoffOutcomes ?? []).Where(id => id != key).Append(key).TakeLast(Capacity)],
        }).ConfigureAwait(false);
    }

    async Task<IReadOnlySet<string>> LoadAsync() =>
        (await state.LoadAsync().ConfigureAwait(false)).DismissedHandoffOutcomes is { Count: > 0 } ids
            ? ids.ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet<string>.Empty;
}
