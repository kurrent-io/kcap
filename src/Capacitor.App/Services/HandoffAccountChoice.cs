namespace Capacitor.App.Services;

/// The account a hand-off draft was sent with; a null Id is the daemon's default.
public sealed record HandoffAccountChoice(string? Id);
