using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// Renders the showcase scene once per theme and once per font into KCAP_GALLERY_OUT; skipped when it is unset.
[NotInParallel("AvaloniaSession")]
public sealed class AppearanceGallery {
    const string OutEnvVar = "KCAP_GALLERY_OUT";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Every_theme_and_font_renders_the_showcase() {
        var dir = Environment.GetEnvironmentVariable(OutEnvVar);
        Skip.When(dir is null, $"Appearance gallery: set {OutEnvVar} to an existing directory");

        var looks = new List<(string Name, AppearanceChoice Choice)>();
        var ui = AppFont.FindUi(AppFont.DefaultUi);
        var code = AppFont.FindCode(AppFont.DefaultCode);
        foreach (var theme in AppTheme.All)
            looks.Add(($"theme-{theme.Id}", new AppearanceChoice(theme,
                theme.Id == AppTheme.ClassicId ? AppFont.FindUi("inter") : ui,
                theme.Id == AppTheme.ClassicId ? AppFont.FindCode("menlo") : code)));
        var graphite = AppTheme.Find("graphite");
        foreach (var font in AppFont.Ui) looks.Add(($"ui-{font.Id}", new AppearanceChoice(graphite, font, code)));
        foreach (var font in AppFont.Code) looks.Add(($"code-{font.Id}", new AppearanceChoice(graphite, ui, font)));

        var shots = looks.Select(l => (l.Name, l.Choice, Build: (Func<Task<DocsShotScene>>)DocsShotScenes.ShowcaseAsync, Ready: (Func<Window, bool>)Ready)).ToList();
        foreach (var theme in new[] { "graphite", "classic" }) {
            var t = AppTheme.Find(theme);
            var choice = theme == "classic" ? new AppearanceChoice(t, AppFont.FindUi("inter"), AppFont.FindCode("menlo")) : new AppearanceChoice(t, ui, code);
            shots.Add(($"settings-appearance-{theme}", choice,
                () => DocsShotScenes.SettingsAsync(Config, "AppearanceTab", height: 640, appearance: choice),
                w => w.GetVisualDescendants().OfType<Control>().Any(c => c.Name is "TextSizeText" && c.IsEffectivelyVisible)));
        }

        foreach (var (name, choice, build, ready) in shots) {
            await AvaloniaSession.DispatchAsync(async () => {
                new AppearanceApplier(Application.Current!).Apply(choice);
                await using var scene = await build();
                var window = scene.Window;
                window.Show();
                window.SetRenderScaling(2);
                for (var i = 0; i < 200 && !(ready(window) && scene.Settled()); i++) {
                    Pump(window);
                    await Task.Delay(5);
                }
                if (window.FocusManager?.GetFocusedElement() is TextBox) window.FocusManager.Focus(null);
                Pump(window);
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir!, $"{name}.png"), new PngBitmapEncoderOptions());
                return true;
            });
        }
        await AvaloniaSession.DispatchAsync(() => {
            new AppearanceApplier(Application.Current!).Apply(new AppearanceChoice(AppTheme.Find(AppTheme.ClassicId), AppFont.FindUi("inter"), AppFont.FindCode("menlo")));
            return Task.FromResult(true);
        });
    }

    static bool Ready(Window window) =>
        window.GetVisualDescendants().OfType<Control>().Any(c => c.Name is "PlanTaskList" && c.IsEffectivelyVisible)
        && window.GetVisualDescendants().OfType<Control>().Any(c => c.Name is "PermissionCard" && c.IsEffectivelyVisible);

    static void Pump(Window window) {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }
}
