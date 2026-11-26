using RxUnit = System.Reactive.Unit;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.ViewModels;

[NotInParallel("AvaloniaSession")]
public class DesktopColorSchemeSettingsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static DesktopColorSchemeSettings Scheme(IAppStateStore store, string? preference,
            Func<DesktopTheme, bool> apply) => new(store, preference, apply, () => true, Observable.Never<RxUnit>());

    [Test]
    [Arguments(null, true, true)]
    [Arguments(null, false, false)]
    [Arguments("light", true, false)]
    [Arguments("dark", false, true)]
    public Task The_saved_scheme_paints_as_soon_as_it_is_built(string? preference, bool systemIsDark, bool paintsDark) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var applied = new List<DesktopTheme>();
            using var scheme = new DesktopColorSchemeSettings(new AppStateStore(Tmp.PathTo("state.json")), preference,
                theme => { applied.Add(theme); return true; }, () => systemIsDark, Observable.Never<RxUnit>());
            await Assert.That(applied.SequenceEqual([paintsDark ? DesktopTheme.Dark : DesktopTheme.Light])).IsTrue();
        });

    [Test]
    public Task Auto_follows_system_changes_only_while_selected() => AvaloniaSession.RunOnUiAsync(async () => {
        using var changes = new Subject<RxUnit>();
        var dark = false;
        var applied = new List<DesktopTheme>();
        var store = new AppStateStore(Tmp.PathTo("state.json"));
        using (var scheme = new DesktopColorSchemeSettings(store, null, theme => { applied.Add(theme); return true; }, () => dark, changes)) {
            await Assert.That(scheme.Selected).IsEqualTo(DesktopColorScheme.Auto);
            dark = true;
            changes.OnNext(RxUnit.Default);
            changes.OnNext(RxUnit.Default);
            await Assert.That(applied.SequenceEqual([DesktopTheme.Light, DesktopTheme.Dark])).IsTrue();
            await scheme.SelectAsync(DesktopColorScheme.Dark);
            dark = false;
            changes.OnNext(RxUnit.Default);
            await Assert.That(changes.HasObservers).IsFalse();
            await Assert.That(applied.Count).IsEqualTo(2);
            await scheme.SelectAsync(DesktopColorScheme.Auto);
            await Assert.That(applied[^1]).IsEqualTo(DesktopTheme.Light);
            await Assert.That((await store.LoadAsync()).DesktopColorScheme).IsEqualTo("auto");
        }
        await Assert.That(changes.HasObservers).IsFalse();
    });

    [Test]
    public Task Rapid_theme_choices_persist_the_last_selection() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new DeferredSave();
        using var scheme = Scheme(store, null, _ => true);
        var first = scheme.SelectAsync(DesktopColorScheme.Light);
        var last = scheme.SelectAsync(DesktopColorScheme.Dark);
        store.Saved.SetResult(true);
        await Task.WhenAll(first, last);
        await Assert.That(scheme.Selected).IsEqualTo(DesktopColorScheme.Dark);
        await Assert.That(store.State.DesktopColorScheme).IsEqualTo("dark");
    });

    [Test]
    public Task A_theme_change_recolors_the_open_window_and_keeps_its_focus_and_selection() => AvaloniaSession.RunOnUiAsync(async () => {
        var app = Application.Current!;
        var palette = new DesktopPalette(app);
        var input = new TextBox { Text = "Check the failed build", Classes = { "kcapField" } };
        var window = new Window { Content = input };
        using var scheme = Scheme(new AppStateStore(Tmp.PathTo("state.json")), "dark", palette.TryApply);
        window.Show();
        try {
            input.Focus(NavigationMethod.Tab);
            input.SelectionStart = 6;
            input.SelectionEnd = 12;
            var dark = ((ISolidColorBrush)input.Foreground!).Color;
            await scheme.SelectAsync(DesktopColorScheme.Light);
            await Assert.That(app.RequestedThemeVariant).IsEqualTo(ThemeVariant.Light);
            await Assert.That(((ISolidColorBrush)input.Foreground!).Color).IsNotEqualTo(dark);
            await Assert.That(input.SelectedText).IsEqualTo("the fa");
            await scheme.SelectAsync(DesktopColorScheme.Dark);
            await Assert.That(app.RequestedThemeVariant).IsEqualTo(ThemeVariant.Dark);
            await Assert.That(((ISolidColorBrush)input.Foreground!).Color).IsEqualTo(dark);
            await Assert.That(input.IsFocused).IsTrue();
            await Assert.That(input.SelectedText).IsEqualTo("the fa");
        } finally {
            window.Close();
            palette.TryApply(DesktopTheme.Light);
        }
    });

    [Test]
    public Task A_theme_reopens_from_desktop_state_and_preserves_other_settings() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new AppStateStore(Tmp.PathTo("state.json"));
        using var scheme = Scheme(store, "unknown", _ => true);
        await Assert.That(scheme.Selected).IsEqualTo(DesktopColorScheme.Auto);
        await Task.WhenAll(scheme.SelectAsync(DesktopColorScheme.Light), store.UpdateAsync(state => state with { UiScale = 1.3 }));
        var saved = await store.LoadAsync();
        await Assert.That(saved.UiScale).IsEqualTo(1.3);
        DesktopTheme? applied = null;
        using var reopened = Scheme(store, saved.DesktopColorScheme, value => { applied = value; return true; });
        await Assert.That(reopened.Selected).IsEqualTo(DesktopColorScheme.Light);
        await Assert.That(applied).IsEqualTo(DesktopTheme.Light);
    });

    [Test]
    public Task Both_themes_recolor_every_shared_brush_in_place() => AvaloniaSession.RunOnUiAsync(async () => {
        var app = Application.Current!;
        var palette = new DesktopPalette(app);
        var light = (ResourceDictionary)app.FindResource(DesktopPalette.LightTheme)!;
        var dark = (ResourceDictionary)app.FindResource(DesktopPalette.DarkTheme)!;
        await Assert.That(light.Keys.ToHashSet().SetEquals(dark.Keys)).IsTrue();
        var brushes = light.Keys.ToDictionary(key => key, key => (SolidColorBrush)app.FindResource(key)!);
        try {
            foreach (var theme in new[] { DesktopTheme.Dark, DesktopTheme.Light, DesktopTheme.Dark }) {
                await Assert.That(palette.TryApply(theme)).IsTrue();
                var expected = theme == DesktopTheme.Light ? light : dark;
                foreach (var (key, brush) in brushes) {
                    await Assert.That(ReferenceEquals(app.FindResource(key), brush)).IsTrue();
                    await Assert.That(brush.Color).IsEqualTo((Color)expected[key]!);
                }
            }
        } finally {
            palette.TryApply(DesktopTheme.Light);
        }
    });

    /// <summary>
    /// The PR roles repeat another role's colour rather than reference it, so each theme must keep
    /// them equal to their source: merged reads as the purple role, open as success.
    /// </summary>
    [Test]
    [Arguments(DesktopPalette.LightTheme, "KcapPrOpenBrush", "KcapSuccessBrush")]
    [Arguments(DesktopPalette.LightTheme, "KcapPrMergedBrush", "KcapPurpleBrush")]
    [Arguments(DesktopPalette.LightTheme, "KcapPrMergedTipBrush", "KcapPurpleBrush")]
    [Arguments(DesktopPalette.LightTheme, "KcapPrClosedBrush", "KcapDangerBrush")]
    [Arguments(DesktopPalette.DarkTheme, "KcapPrOpenBrush", "KcapSuccessBrush")]
    [Arguments(DesktopPalette.DarkTheme, "KcapPrMergedBrush", "KcapPurpleBrush")]
    [Arguments(DesktopPalette.DarkTheme, "KcapPrMergedTipBrush", "KcapPurpleBrush")]
    [Arguments(DesktopPalette.DarkTheme, "KcapPrClosedBrush", "KcapDangerBrush")]
    public Task A_status_role_keeps_the_colour_of_the_role_it_follows(string palette, string role, string source) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var theme = (ResourceDictionary)Application.Current!.FindResource(palette)!;
            await Assert.That((Color)theme[role]!).IsEqualTo((Color)theme[source]!);
        });

    [Test]
    public Task An_incomplete_palette_keeps_the_open_window_in_its_previous_theme() => AvaloniaSession.RunOnUiAsync(async () => {
        var app = Application.Current!;
        var light = (ResourceDictionary)app.FindResource(DesktopPalette.LightTheme)!;
        var palette = new DesktopPalette(app);
        palette.TryApply(DesktopTheme.Dark);
        var canvas = (SolidColorBrush)app.FindResource("KcapCanvasBrush")!;
        var dark = canvas.Color;
        light.Add("MissingPreviewBrush", Colors.White);
        try {
            using var scheme = Scheme(new AppStateStore(Tmp.PathTo("state.json")), "light", palette.TryApply);
            await Assert.That(scheme.Selected).IsEqualTo(DesktopColorScheme.Light);
            await Assert.That(scheme.Problem).IsNotNullOrEmpty();
            await Assert.That(canvas.Color).IsEqualTo(dark);
            await Assert.That(app.RequestedThemeVariant).IsEqualTo(ThemeVariant.Dark);
        } finally {
            light.Remove("MissingPreviewBrush");
            palette.TryApply(DesktopTheme.Light);
        }
    });

    [Test]
    public Task A_saved_theme_that_cannot_load_can_be_retried_from_the_visible_theme() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new AppStateStore(Tmp.PathTo("state.json"));
        var lightAvailable = false;
        using var scheme = Scheme(store, null, theme => theme != DesktopTheme.Light || lightAvailable);
        await scheme.SelectAsync(DesktopColorScheme.Light);
        await Assert.That(scheme.Selected).IsEqualTo(DesktopColorScheme.Auto);
        await Assert.That(scheme.Problem).IsNotNullOrEmpty();
        lightAvailable = true;
        await scheme.SelectAsync(DesktopColorScheme.Light);
        await Assert.That(scheme.Selected).IsEqualTo(DesktopColorScheme.Light);
        await Assert.That(scheme.Problem).IsNull();
    });

    [Test]
    public Task A_save_failure_after_shutdown_cannot_reopen_the_system_watcher() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new DeferredSave();
        using var changes = new Subject<RxUnit>();
        var scheme = new DesktopColorSchemeSettings(store, null, _ => true, () => true, changes);
        var pending = scheme.SelectAsync(DesktopColorScheme.Light);
        scheme.Dispose();
        store.Saved.SetResult(false);
        await pending;
        await Assert.That(changes.HasObservers).IsFalse();
    });

    sealed class DeferredSave : IAppStateStore {
        public TaskCompletionSource<bool> Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AppState> LoadAsync() => Task.FromResult(new AppState());
        public AppState State { get; private set; } = new();
        public async Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            if (!await Saved.Task) return false;
            State = mutate(State);
            return true;
        }
    }

    [Test]
    public Task A_failed_save_restores_the_saved_choice_and_reports_the_failure() => AvaloniaSession.RunOnUiAsync(async () => {
        DesktopTheme? applied = null;
        var store = new AppStateStore(Tmp.CreateDir("directory-in-place-of-state-file"));
        using var scheme = Scheme(store, null, value => { applied = value; return true; });
        await scheme.SelectAsync(DesktopColorScheme.Light);
        await Assert.That(applied).IsEqualTo(DesktopTheme.Dark);
        await Assert.That(scheme.Selected).IsEqualTo(DesktopColorScheme.Auto);
        await Assert.That(scheme.Problem).IsNotNullOrEmpty();
    });
}
