using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Page is null on every failure, a server without the route included: it answers 404 like a missing session.
public sealed record PendingInterruptsFetch(PendingInterruptsPage? Page, bool NotFound = false, bool Unauthorized = false, string? Failure = null);
