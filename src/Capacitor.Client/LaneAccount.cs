namespace Capacitor.Client;

/// The account the lane last connected as, to tell a sign-in as someone else from a reconnect.
/// Not thread-safe: the caller serializes the statuses it passes.
public sealed class LaneAccount {
    string? _subject;

    /// Records the account of a connected status that names one; true when it differs from the last one named.
    public bool SwitchedBy(ServerLaneStatus status) {
        if (status.State != ServerLaneState.Connected || status.Subject is not { } subject) return false;

        var switched = _subject is not null && _subject != subject;
        _subject = subject;

        return switched;
    }
}
