namespace Capacitor.App.ViewModels;

/// A saved topic: whether it was folded, and the name the user gave it, if any.
public readonly record struct ChatTopicMark(bool Folded, string? Name);
