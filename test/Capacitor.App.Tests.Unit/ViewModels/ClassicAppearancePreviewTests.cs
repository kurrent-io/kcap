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
public class ClassicAppearancePreviewTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static ClassicAppearancePreview Preview(IAppStateStore store, string? preference,
            Func<ClassicAppearance, bool> apply) => new(store, preference, apply, () => true, Observable.Never<RxUnit>());

    [Test]
    public Task Auto_follows_system_changes_only_while_enabled_and_selected() => AvaloniaSession.RunOnUiAsync(async () => {
        using var changes = new Subject<RxUnit>();
        var dark = false;
        var applied = new List<ClassicAppearance>();
        var store = new AppStateStore(Tmp.PathTo("state.json"));
        using var preview = new ClassicAppearancePreview(store, null, theme => { applied.Add(theme); return true; }, () => dark, changes);
        preview.SetEnabled(true);
        await Assert.That(preview.Selected).IsEqualTo(DesktopColorScheme.Auto);
        dark = true;
        changes.OnNext(RxUnit.Default);
        changes.OnNext(RxUnit.Default);
        await Assert.That(applied.SequenceEqual([ClassicAppearance.Light, ClassicAppearance.Dark])).IsTrue();
        await preview.SelectAsync(DesktopColorScheme.Dark);
        dark = false;
        changes.OnNext(RxUnit.Default);
        await Assert.That(changes.HasObservers).IsFalse();
        await Assert.That(applied.Count).IsEqualTo(2);
        await preview.SelectAsync(DesktopColorScheme.Auto);
        await Assert.That(applied[^1]).IsEqualTo(ClassicAppearance.Light);
        await Assert.That((await store.LoadAsync()).DesktopColorScheme).IsEqualTo("auto");
        preview.SetEnabled(false);
        await Assert.That(changes.HasObservers).IsFalse();
        dark = true;
        preview.SetEnabled(true);
        await Assert.That(applied[^1]).IsEqualTo(ClassicAppearance.Dark);
        preview.Dispose();
        await Assert.That(changes.HasObservers).IsFalse();
    });

    [Test]
    public Task Rapid_theme_choices_persist_the_last_selection() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new DeferredSave();
        using var preview = Preview(store, null, _ => true);
        preview.SetEnabled(true);
        var first = preview.SelectAsync(DesktopColorScheme.Light);
        var last = preview.SelectAsync(DesktopColorScheme.Dark);
        store.Saved.SetResult(true);
        await Task.WhenAll(first, last);
        await Assert.That(preview.Selected).IsEqualTo(DesktopColorScheme.Dark);
        await Assert.That(store.State.DesktopColorScheme).IsEqualTo("dark");
    });

    [Test]
    public Task Disabling_restores_the_open_window_and_keeps_the_draft_selection() => AvaloniaSession.RunOnUiAsync(async () => {
        var app = Application.Current!;
        var palette = new ClassicPreviewPalette(app);
        var input = new TextBox { Text = "Check the failed build", Classes = { "kcapField" } };
        var window = new Window { Content = input };
        using var preview = Preview(new AppStateStore(Tmp.PathTo("state.json")), null, palette.TryApply);
        window.Show();
        try {
            input.Focus(NavigationMethod.Tab);
            input.SelectionStart = 6;
            input.SelectionEnd = 12;
            var original = ((ISolidColorBrush)input.Foreground!).Color;
            await preview.SelectAsync(DesktopColorScheme.Light);
            await Assert.That(app.RequestedThemeVariant).IsEqualTo(ThemeVariant.Dark);
            preview.SetEnabled(true);
            await preview.SelectAsync(DesktopColorScheme.Light);
            await Assert.That(app.RequestedThemeVariant).IsEqualTo(ThemeVariant.Light);
            await Assert.That(((ISolidColorBrush)input.Foreground!).Color).IsNotEqualTo(original);
            await Assert.That(input.SelectedText).IsEqualTo("the fa");
            preview.SetEnabled(false);
            await Assert.That(app.RequestedThemeVariant).IsEqualTo(ThemeVariant.Dark);
            await Assert.That(((ISolidColorBrush)input.Foreground!).Color).IsEqualTo(original);
            await Assert.That(input.IsFocused).IsTrue();
            await Assert.That(input.SelectedText).IsEqualTo("the fa");
        } finally {
            preview.SetEnabled(false);
            window.Close();
        }
    });

    [Test]
    public Task A_theme_reopens_from_desktop_state_and_preserves_other_settings() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new AppStateStore(Tmp.PathTo("state.json"));
        using var preview = Preview(store, "unknown", _ => true);
        preview.SetEnabled(true);
        await Assert.That(preview.Selected).IsEqualTo(DesktopColorScheme.Auto);
        await Task.WhenAll(preview.SelectAsync(DesktopColorScheme.Light), store.UpdateAsync(state => state with { UiScale = 1.3 }));
        var saved = await store.LoadAsync();
        await Assert.That(saved.UiScale).IsEqualTo(1.3);
        var applied = ClassicAppearance.Original;
        using var reopened = Preview(store, saved.DesktopColorScheme, value => { applied = value; return true; });
        reopened.SetEnabled(true);
        await Assert.That(applied).IsEqualTo(ClassicAppearance.Light);
        reopened.SetEnabled(false);
        await Assert.That(applied).IsEqualTo(ClassicAppearance.Original);
        reopened.SetEnabled(true);
        await Assert.That(applied).IsEqualTo(ClassicAppearance.Light);
    });

    [Test]
    public Task Both_palettes_restore_every_original_brush_on_repeated_rollback() => AvaloniaSession.RunOnUiAsync(async () => {
        var app = Application.Current!;
        var palette = new ClassicPreviewPalette(app);
        var light = (ResourceDictionary)app.FindResource("KcapClassicLight")!;
        var dark = (ResourceDictionary)app.FindResource("KcapClassicDark")!;
        await Assert.That(light.Keys.ToHashSet().SetEquals(dark.Keys)).IsTrue();
        var brushes = light.Keys.ToDictionary(key => key, key => (SolidColorBrush)app.FindResource(key)!);
        var originals = brushes.ToDictionary(pair => pair.Key, pair => pair.Value.Color);
        var variant = app.RequestedThemeVariant;
        try {
            for (var run = 0; run < 2; run++) {
                foreach (var theme in new[] { ClassicAppearance.Dark, ClassicAppearance.Light, ClassicAppearance.Dark }) {
                    await Assert.That(palette.TryApply(theme)).IsTrue();
                    var expected = theme == ClassicAppearance.Light ? light : dark;
                    foreach (var (key, brush) in brushes) {
                        await Assert.That(ReferenceEquals(app.FindResource(key), brush)).IsTrue();
                        await Assert.That(brush.Color).IsEqualTo((Color)expected[key]!);
                    }
                }
                await Assert.That(palette.TryApply(ClassicAppearance.Original)).IsTrue();
                await Assert.That(app.RequestedThemeVariant).IsEqualTo(variant);
                foreach (var (key, color) in originals)
                    await Assert.That(((SolidColorBrush)app.FindResource(key)!).Color).IsEqualTo(color);
            }
        } finally {
            palette.TryApply(ClassicAppearance.Original);
        }
    });

    [Test]
    public Task An_incomplete_palette_keeps_the_open_window_in_its_previous_theme() => AvaloniaSession.RunOnUiAsync(async () => {
        var app = Application.Current!;
        var light = (ResourceDictionary)app.FindResource("KcapClassicLight")!;
        var palette = new ClassicPreviewPalette(app);
        var variant = app.RequestedThemeVariant;
        var canvas = (SolidColorBrush)app.FindResource("KcapCanvasBrush")!;
        var original = canvas.Color;
        light.Add("MissingPreviewBrush", Colors.White);
        try {
            using var preview = Preview(new AppStateStore(Tmp.PathTo("state.json")), "light", palette.TryApply);
            preview.SetEnabled(true);
            await Assert.That(preview.Selected).IsEqualTo(DesktopColorScheme.Light);
            await Assert.That(preview.Problem).IsNotNullOrEmpty();
            await Assert.That(canvas.Color).IsEqualTo(original);
            await Assert.That(app.RequestedThemeVariant).IsEqualTo(variant);
        } finally {
            light.Remove("MissingPreviewBrush");
            palette.TryApply(ClassicAppearance.Original);
        }
    });

    [Test]
    public Task A_saved_theme_that_cannot_load_can_be_retried_from_the_visible_theme() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new AppStateStore(Tmp.PathTo("state.json"));
        var lightAvailable = false;
        using var preview = Preview(store, null, theme => theme != ClassicAppearance.Light || lightAvailable);
        preview.SetEnabled(true);
        await preview.SelectAsync(DesktopColorScheme.Light);
        await Assert.That(preview.Selected).IsEqualTo(DesktopColorScheme.Auto);
        await Assert.That(preview.Problem).IsNotNullOrEmpty();
        lightAvailable = true;
        await preview.SelectAsync(DesktopColorScheme.Light);
        await Assert.That(preview.Selected).IsEqualTo(DesktopColorScheme.Light);
        await Assert.That(preview.Problem).IsNull();
    });

    [Test]
    public Task Disabling_during_a_save_keeps_the_preference_for_the_next_opt_in() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new DeferredSave();
        var changes = new List<ClassicAppearance>();
        using var preview = Preview(store, null, value => { changes.Add(value); return true; });
        preview.SetEnabled(true);
        var pending = preview.SelectAsync(DesktopColorScheme.Light);
        preview.SetEnabled(false);
        store.Saved.SetResult(true);
        await pending;
        await Assert.That(changes.SequenceEqual([ClassicAppearance.Dark, ClassicAppearance.Light, ClassicAppearance.Original])).IsTrue();
        preview.SetEnabled(true);
        await Assert.That(changes[^1]).IsEqualTo(ClassicAppearance.Light);
    });

    [Test]
    public Task A_save_failure_after_shutdown_cannot_reopen_the_system_watcher() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new DeferredSave();
        using var changes = new Subject<RxUnit>();
        var preview = new ClassicAppearancePreview(store, null, _ => true, () => true, changes);
        preview.SetEnabled(true);
        var pending = preview.SelectAsync(DesktopColorScheme.Light);
        preview.Dispose();
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
        var applied = ClassicAppearance.Original;
        var store = new AppStateStore(Tmp.CreateDir("directory-in-place-of-state-file"));
        using var preview = Preview(store, null, value => { applied = value; return true; });
        preview.SetEnabled(true);
        await preview.SelectAsync(DesktopColorScheme.Light);
        await Assert.That(applied).IsEqualTo(ClassicAppearance.Dark);
        await Assert.That(preview.Selected).IsEqualTo(DesktopColorScheme.Auto);
        await Assert.That(preview.Problem).IsNotNullOrEmpty();
    });
}
