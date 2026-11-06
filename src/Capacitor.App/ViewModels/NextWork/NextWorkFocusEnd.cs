namespace Capacitor.App.ViewModels.NextWork;

/// <summary>A loose end under a focus item. <see cref="Repeats"/> counts the near-identical notes folded into it.</summary>
public sealed record NextWorkFocusEnd(string? LooseEndId, string Text, string? SessionId, string? Noted, int Repeats);
