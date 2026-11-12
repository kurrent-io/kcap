using Avalonia.Media;

namespace Capacitor.App.Services;

/// A palette for the Kcap* brushes plus whether the soft component styles apply. Classic carries no
/// palette: it restores the colors App.axaml declares.
public sealed record AppTheme(string Id, string Label, bool Soft, bool Light, IReadOnlyDictionary<string, Color> Palette) {
    public const string ClassicId = "classic";
    public const string DefaultId = "graphite";

    public static AppTheme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All.First(t => t.Id == DefaultId);

    static AppTheme Dark(string id, string label,
        string canvas, string surface, string raised, string border, string borderStrong,
        string text, string muted, string faint, string selection,
        string success = "#74CDAE", string successDim = "#1B3530",
        string warning = "#E2B477", string warningDim = "#3A3022",
        string purple = "#A99BEA", string purpleDim = "#27243A",
        string danger = "#E88A8A", string dangerDim = "#352326",
        string info = "#8FB0EA", string infoDim = "#1D2535") =>
        new(id, label, Soft: true, Light: false, BuildPalette(canvas, surface, raised, border, borderStrong, text, muted, faint,
            selection, success, successDim, warning, warningDim, purple, purpleDim, danger, dangerDim, info, infoDim));

    static Dictionary<string, Color> BuildPalette(
        string canvas, string surface, string raised, string border, string borderStrong,
        string text, string muted, string faint, string selection,
        string success, string successDim, string warning, string warningDim,
        string purple, string purpleDim, string danger, string dangerDim, string info, string infoDim) => new() {
        ["KcapCanvasBrush"] = Color.Parse(canvas),
        ["KcapSurfaceBrush"] = Color.Parse(surface),
        ["KcapSurfaceRaisedBrush"] = Color.Parse(raised),
        ["KcapBorderBrush"] = Color.Parse(border),
        ["KcapBorderStrongBrush"] = Color.Parse(borderStrong),
        ["KcapTextBrush"] = Color.Parse(text),
        ["KcapMutedBrush"] = Color.Parse(muted),
        ["KcapFaintBrush"] = Color.Parse(faint),
        ["KcapPrimaryBrush"] = Color.Parse(text),
        ["KcapPrimaryHoverBrush"] = Color.Parse(text),
        ["KcapPrimaryPressedBrush"] = Color.Parse(muted),
        ["KcapOnPrimaryBrush"] = Color.Parse(canvas),
        ["KcapSuccessBrush"] = Color.Parse(success),
        ["KcapSuccessDimBrush"] = Color.Parse(successDim),
        ["KcapWarningBrush"] = Color.Parse(warning),
        ["KcapWarningDimBrush"] = Color.Parse(warningDim),
        ["KcapPurpleBrush"] = Color.Parse(purple),
        ["KcapPurpleDimBrush"] = Color.Parse(purpleDim),
        ["KcapDangerBrush"] = Color.Parse(danger),
        ["KcapDangerDimBrush"] = Color.Parse(dangerDim),
        ["KcapInfoBrush"] = Color.Parse(info),
        ["KcapInfoDimBrush"] = Color.Parse(infoDim),
        ["KcapSelectionBrush"] = Color.Parse(selection),
        ["ScrollBarPanningThumbBackground"] = Color.Parse(borderStrong),
        ["ScrollBarThumbFillPointerOver"] = Color.Parse(faint),
        ["ScrollBarThumbFillPressed"] = Color.Parse(muted),
        ["ScrollBarTrackFillPointerOver"] = Color.Parse(raised),
        ["ScrollBarTrackStrokePointerOver"] = Color.Parse(border),
    };

    public static IReadOnlyList<AppTheme> All { get; } = [
        new(ClassicId, "Classic", Soft: false, Light: false, new Dictionary<string, Color>()),
        // Neutral grey with a faint warm bias; panes differ by tone, borders nearly vanish.
        Dark("graphite", "Graphite",
            canvas: "#16171A", surface: "#1A1B1F", raised: "#222328", border: "#26282D", borderStrong: "#383A41",
            text: "#E7E7EA", muted: "#A6A7AE", faint: "#87888F", selection: "#2A2B31"),
        // Today's blue-grey, lifted off black and desaturated.
        Dark("slate", "Slate",
            canvas: "#14171D", surface: "#181B22", raised: "#1F232B", border: "#252A33", borderStrong: "#363C48",
            text: "#E5E8EE", muted: "#A3AAB7", faint: "#848B98", selection: "#232A38", info: "#8EAEEB"),
        // One near-black plane: the rails share the canvas, so only spacing separates them.
        Dark("ink", "Ink",
            canvas: "#0F1012", surface: "#0F1012", raised: "#18191C", border: "#1D1E22", borderStrong: "#2E3035",
            text: "#E4E4E6", muted: "#A0A1A7", faint: "#7F8087", selection: "#1E1F23"),
        Dark("moss", "Moss",
            canvas: "#141714", surface: "#181B18", raised: "#1F2320", border: "#252A26", borderStrong: "#363D38",
            text: "#E5E9E4", muted: "#A4ABA3", faint: "#858C84", selection: "#232A24",
            success: "#8BCFA8", purple: "#B5A5E0", info: "#94B6D6"),
        Dark("dusk", "Dusk",
            canvas: "#18161A", surface: "#1C1A1F", raised: "#242128", border: "#2A262D", borderStrong: "#3C3741",
            text: "#ECE8EE", muted: "#ACA5B1", faint: "#8C8592", selection: "#2D2833",
            warning: "#E8B98A", purple: "#B9A3EE"),
        new("paper", "Paper (light, experimental)", Soft: true, Light: true, BuildPalette(
            canvas: "#F7F7F5", surface: "#F0F0EC", raised: "#E8E8E3", border: "#E0E0DA", borderStrong: "#C8C8C1",
            text: "#1F2023", muted: "#55575E", faint: "#6E7078", selection: "#E1E4EA",
            success: "#1E8A68", successDim: "#DDF0E8", warning: "#A86A12", warningDim: "#F6EBD8",
            purple: "#6A55C9", purpleDim: "#ECE8F8", danger: "#C24646", dangerDim: "#F7E3E3",
            info: "#3767B8", infoDim: "#E3EAF6")),
    ];
}
