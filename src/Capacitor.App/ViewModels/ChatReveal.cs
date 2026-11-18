namespace Capacitor.App.ViewModels;

/// A row the chat is asked to show, and what in it to pick out: <paramref name="Quote"/>, all of
/// its text when <paramref name="Whole"/>, or nothing.
public sealed record ChatReveal(ChatItemViewModel Row, string? Quote, bool Whole = false);
