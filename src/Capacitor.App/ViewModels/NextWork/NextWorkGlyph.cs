namespace Capacitor.App.ViewModels.NextWork;

/// <summary>A row's kind at a glance. <see cref="Badge"/> is <c>wait</c>, <c>ok</c> or <c>stop</c>, or null.</summary>
public sealed record NextWorkGlyph(string Icon, NextWorkGroup Tint, string? Badge);
