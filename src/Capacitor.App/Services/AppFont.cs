namespace Capacitor.App.Services;

/// A selectable face for UI text or for code. Embedded faces live under Assets/Fonts, one folder per family.
public sealed record AppFont(string Id, string Label, string Family) {
    const string Embedded = "avares://kcap-desktop/Assets/Fonts/";

    public const string DefaultUi = "figtree";
    public const string DefaultCode = "source-code-pro";

    public static IReadOnlyList<AppFont> Ui { get; } = [
        new("inter", "Inter", "fonts:Inter#Inter"),
        new("figtree", "Figtree", Embedded + "Figtree#Figtree"),
        new("dm-sans", "DM Sans", Embedded + "DMSans#DM Sans"),
    ];

    public static IReadOnlyList<AppFont> Code { get; } = [
        new("menlo", "Menlo", "Menlo,Monaco,Consolas,Cascadia Mono,DejaVu Sans Mono,monospace"),
        new("source-code-pro", "Source Code Pro", Embedded + "SourceCodePro#Source Code Pro"),
        new("roboto-mono", "Roboto Mono", Embedded + "RobotoMono#Roboto Mono"),
    ];

    public static AppFont FindUi(string? id) => Ui.FirstOrDefault(f => f.Id == id) ?? Ui.First(f => f.Id == DefaultUi);
    public static AppFont FindCode(string? id) => Code.FirstOrDefault(f => f.Id == id) ?? Code.First(f => f.Id == DefaultCode);
}
