namespace Capacitor.App.ViewModels;

/// A row the chat is asked to show, and the text in it to pick out, if any.
public sealed record ChatReveal(ChatItemViewModel Row, string? Quote);
