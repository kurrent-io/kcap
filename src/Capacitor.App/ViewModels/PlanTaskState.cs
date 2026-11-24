namespace Capacitor.App.ViewModels;

public enum PlanTaskState { Pending, InProgress, Completed, Skipped }

internal static class PlanTaskStatePresentation {
    public static StatusGlyphKind Glyph(this PlanTaskState state, bool active) => state switch {
        PlanTaskState.Pending => StatusGlyphKind.Pending,
        PlanTaskState.InProgress => active ? StatusGlyphKind.Activity : StatusGlyphKind.Stopped,
        PlanTaskState.Completed => StatusGlyphKind.Success,
        PlanTaskState.Skipped => StatusGlyphKind.Skipped,
        _ => StatusGlyphKind.Unknown,
    };
}
