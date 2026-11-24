namespace Capacitor.App.ViewModels;

/// What the rail footer says when something needs attention. A healthy stack has no alert at all.
/// The launcher's banner owns the repairing actions; the rail only names the state.
/// <param name="Tone">A <see cref="StatusColors"/> value.</param>
/// <param name="Detail">The hover: the longer explanation the one-line text leaves out.</param>
public sealed record RailAlert(string Text, string Tone, string? Detail = null) {
    public bool IsActionable => Glyph is StatusGlyphKind.Attention or StatusGlyphKind.Failure;

    public StatusGlyphKind Glyph => Tone switch {
        StatusColors.Connected => StatusGlyphKind.Healthy,
        StatusColors.Pending => StatusGlyphKind.Activity,
        StatusColors.InProgress => StatusGlyphKind.Attention,
        StatusColors.Disrupted => StatusGlyphKind.Failure,
        _ => StatusGlyphKind.Unknown,
    };
}
