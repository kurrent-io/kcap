namespace Capacitor.App.Services;

/// A session's unresolved interrupts as the server folds them, with the position to continue folding from.
public delegate Task<PendingInterruptsFetch> PendingInterruptsReader(string sessionId, CancellationToken ct);
