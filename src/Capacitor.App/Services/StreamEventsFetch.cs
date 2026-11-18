using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Page is null on every failure; NotFound covers a stream the server has not written yet or a session this
/// caller cannot see, and Failure says what else went wrong, for the log.
public sealed record StreamEventsFetch(StreamEventsPage? Page, bool NotFound = false, bool Unauthorized = false, string? Failure = null);
