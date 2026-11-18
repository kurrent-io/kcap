namespace Capacitor.App.ViewModels;

/// A saved mark at a message: a topic that starts there, whether it was folded and the name the user
/// gave it, or, when <paramref name="End"/>, the end of the topic before.
public readonly record struct ChatTopicMark(bool Folded, string? Name, bool End = false);
