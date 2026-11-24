using Capacitor.App.Services.LocalSetup;

namespace Capacitor.App.ViewModels.Onboarding;

public sealed record InstallStepRow(string Label, InstallRow Row) {
    public static StatusGlyphKind GlyphFor(RowState state) => state switch {
        RowState.Running => StatusGlyphKind.Activity,
        RowState.Done => StatusGlyphKind.Success,
        RowState.Skipped => StatusGlyphKind.Skipped,
        _ => StatusGlyphKind.Pending,
    };

    public static string LabelFor(RowState state) => state switch {
        RowState.Running => "Installing",
        RowState.Done => "Done",
        RowState.Skipped => "Skipped",
        _ => "Pending",
    };
}
