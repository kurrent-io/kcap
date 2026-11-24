namespace Capacitor.App.ViewModels;

public enum RunState { Running, Done, Failed, Stopped }

internal static class RunStatePresentation {
    public static StatusGlyphKind Glyph(this RunState state) => state switch {
        RunState.Running => StatusGlyphKind.Activity,
        RunState.Done => StatusGlyphKind.Success,
        RunState.Failed => StatusGlyphKind.Failure,
        RunState.Stopped => StatusGlyphKind.Stopped,
        _ => StatusGlyphKind.Unknown,
    };
}
