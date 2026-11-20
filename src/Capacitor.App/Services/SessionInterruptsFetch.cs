namespace Capacitor.App.Services;

/// Interrupts is null on every failure; NotFound is the one failure that says the session holds nothing.
public sealed record SessionInterruptsFetch(InterruptReconciliation? Interrupts, bool NotFound = false);
