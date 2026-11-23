using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Capacitor.App.Tests.Unit;

/// The tray icon and status tones carry hex strings; each must be the palette colour of the status it names,
/// so a palette change cannot leave the tray and the window disagreeing.
[NotInParallel("AvaloniaSession")]
public class StatusColorsTests {
    public static IEnumerable<Func<(string Hex, string PaletteKey)>> Tones() => new[] {
        (StatusColors.Connected, AppStatusPalette.Connected),
        (StatusColors.InProgress, AppStatusPalette.Attention),
        (StatusColors.Disrupted, AppStatusPalette.Disrupted),
        (StatusColors.Unavailable, AppStatusPalette.Unavailable),
        (StatusColors.Pending, AppStatusPalette.Pending),
    }.Select(t => (Func<(string, string)>)(() => t));

    [Test]
    [MethodDataSource(nameof(Tones))]
    public Task Each_status_tone_is_its_palette_colour((string Hex, string PaletteKey) tone) => AvaloniaSession.RunOnUiAsync(async () => {
        var palette = ((ISolidColorBrush)Application.Current!.FindResource(tone.PaletteKey)!).Color;
        await Assert.That(palette).IsEqualTo(Color.Parse(tone.Hex));
    });
}
