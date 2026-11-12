using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Capacitor.App.Services;

/// Applies an AppearanceChoice to the running app. Every view binds the Kcap* brushes as
/// StaticResource, so a theme recolours those brush instances in place rather than replacing them.
public sealed class AppearanceApplier {
    readonly Application _app;
    // Captured once per process, before any theme recolours the brushes, so every applier restores the same classic look.
    static readonly Dictionary<string, Color> Classic = [];
    // One sheet and one font style per process, whichever applier added them, so any applier can take them out.
    static Styles? _soft;
    static Style? _uiFont;

    public AppearanceApplier(Application app) {
        _app = app;
        if (Classic.Count == 0)
            foreach (var key in AppTheme.All.SelectMany(t => t.Palette.Keys).Distinct())
                if (Brush(key) is { } brush) Classic[key] = brush.Color;
        _soft ??= (Styles)AvaloniaXamlLoader.Load(new Uri("avares://kcap-desktop/Views/SoftThemeStyles.axaml"));
    }

    public void Apply(AppearanceChoice choice) {
        foreach (var (key, classic) in Classic)
            Brush(key)!.Color = choice.Theme.Palette.TryGetValue(key, out var color) ? color : classic;
        _app.RequestedThemeVariant = choice.Theme.Light ? ThemeVariant.Light : ThemeVariant.Dark;

        var softOn = _app.Styles.Contains(_soft!);
        if (choice.Theme.Soft && !softOn) _app.Styles.Add(_soft!);
        else if (!choice.Theme.Soft && softOn) _app.Styles.Remove(_soft!);

        var ui = new FontFamily(choice.UiFont.Family);
        if (_uiFont is not null) _app.Styles.Remove(_uiFont);
        _uiFont = new Style(x => x.Is<TopLevel>()) { Setters = { new Setter(TemplatedControl.FontFamilyProperty, ui) } };
        _app.Styles.Add(_uiFont);
        // Fluent's control themes set their own FontFamily from this key, so it has to follow too.
        _app.Resources["ContentControlThemeFontFamily"] = ui;
        _app.Resources["KcapMonoFont"] = new FontFamily(choice.CodeFont.Family);

        var soft = choice.Theme.Soft;
        // Soft themes separate filled shapes by tone alone and drop the header and footer rules.
        IBrush border = Brush("KcapBorderBrush")!;
        _app.Resources["KcapFilledBorderBrush"] = soft ? Brushes.Transparent : border;
        _app.Resources["KcapDividerBrush"] = soft ? Brushes.Transparent : border;
        // The rail's rows sit flush under their group heading once the rule beside them is gone.
        _app.Resources["KcapRailBodyPadding"] = soft ? new Thickness(0, 2, 0, 2) : new Thickness(8, 2, 0, 2);
        _app.Resources["KcapRailBodyMargin"] = soft ? new Thickness(-8, 2, -8, 0) : new Thickness(6, 2, 0, 0);
        // Chat leads: one centred reading column with the composer under it on the same width.
        _app.Resources["KcapChatPresenterMargin"] = soft ? new Thickness(40, 28, 40, 0) : new Thickness(22, 18, 22, 0);
        _app.Resources["KcapChatMaxWidth"] = soft ? 760d : double.PositiveInfinity;
        _app.Resources["KcapAgentTextMaxWidth"] = soft ? 760d : 660d;
        _app.Resources["KcapComposerBandBrush"] = soft ? Brush("KcapCanvasBrush")! : Brush("KcapSurfaceBrush")!;
        _app.Resources["KcapComposerBandPadding"] = soft ? new Thickness(40, 6, 40, 20) : new Thickness(22, 15, 22, 16);
        _app.Resources["KcapListTextBrush"] = soft ? Brush("KcapMutedBrush")! : Brush("KcapTextBrush")!;
        _app.Resources["KcapListTitleWeight"] = soft ? FontWeight.Normal : FontWeight.SemiBold;
        SetCalm(soft);
        _app.Resources["KcapCardRadius"] = new CornerRadius(soft ? 14 : 10);
        _app.Resources["KcapSettingsCardRadius"] = new CornerRadius(soft ? 14 : 8);
        _app.Resources["KcapRowRadius"] = new CornerRadius(soft ? 10 : 7);
    }

    // Views style their soft-theme layout under "Window.calm …": a view's own styles outrank
    // anything app-level, so a window class is the one switch they can all key on.
    const string CalmClass = "calm";
    static bool _calm;
    static bool _calmHooked;

    void SetCalm(bool on) {
        _calm = on;
        if (!_calmHooked) {
            _calmHooked = true;
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => window.Classes.Set(CalmClass, _calm));
        }
        if (_app.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var window in desktop.Windows) window.Classes.Set(CalmClass, on);
    }

    SolidColorBrush? Brush(string key) =>
        _app.Resources.TryGetResource(key, ThemeVariant.Dark, out var value) ? value as SolidColorBrush : null;
}
