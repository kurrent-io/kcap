namespace Capacitor.App.ViewModels;

/// One option of a sessions page filter. A null Key matches every session.
public sealed record AllSessionsChoice(string? Key, string Label);
