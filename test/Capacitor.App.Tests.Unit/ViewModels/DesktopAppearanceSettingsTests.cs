using System.Globalization;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using NSubstitute;

namespace Capacitor.App.Tests.Unit.ViewModels;

[NotInParallel("AvaloniaSession")]
public class DesktopAppearanceSettingsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public Task Rapid_edits_keep_other_desktop_preferences_on_restart() => AvaloniaSession.RunOnUiAsync(async () => {
        var path = Tmp.PathTo("state.json");
        var store = new AppStateStore(path);
        await store.UpdateAsync(state => state with {
            DesktopAppearance = new Dictionary<string, string> { ["density"] = "unknown", ["future"] = "keep me" },
        });
        DesktopAppearancePreferences? live = null;
        using var settings = new DesktopAppearanceSettings(store, (await store.LoadAsync()).DesktopAppearance, choice => live = choice);
        await Assert.That(settings.Density).IsEqualTo(DesktopDensity.Comfortable);
        settings.SetEnabled(true);
        settings.ReadingFont = DesktopReadingFont.Monospace;
        settings.HighContrast = true;
        settings.Density = DesktopDensity.Compact;
        await Task.WhenAll(settings.PendingSave, store.UpdateAsync(state => state with { UiScale = 1.3, RailCollapsed = true }));
        await Assert.That(live).IsEqualTo(new DesktopAppearancePreferences(DesktopDensity.Compact, DesktopReadingFont.Monospace, true));

        settings.SetEnabled(false);
        await Assert.That(live).IsNull();
        settings.SetEnabled(true);
        await Assert.That(live!.Density).IsEqualTo(DesktopDensity.Compact);

        var saved = await new AppStateStore(path).LoadAsync();
        await Assert.That(saved.UiScale).IsEqualTo(1.3);
        await Assert.That(saved.RailCollapsed).IsTrue();
        await Assert.That(saved.DesktopAppearance!["future"]).IsEqualTo("keep me");
        using var reopened = new DesktopAppearanceSettings(store, saved.DesktopAppearance, _ => { });
        await Assert.That(reopened.HighContrast).IsTrue();
        await Assert.That(reopened.ReadingFont).IsEqualTo(DesktopReadingFont.Monospace);
        await Assert.That(reopened.Density).IsEqualTo(DesktopDensity.Compact);
    });

    [Test]
    public Task A_failed_write_restores_the_saved_choice_and_can_be_retried() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Substitute.For<IAppStateStore>();
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.UpdateAsync(Arg.Any<Func<AppState, AppState>>()).Returns(first.Task, Task.FromResult(true));
        DesktopAppearancePreferences? live = null;
        using var settings = new DesktopAppearanceSettings(store, null, choice => live = choice);
        settings.SetEnabled(true);
        settings.ReadingFont = DesktopReadingFont.Monospace;
        await Assert.That(live!.ReadingFont).IsEqualTo(DesktopReadingFont.Monospace);
        first.SetResult(false);
        await settings.PendingSave;
        await Assert.That(settings.Problem).IsNotNullOrEmpty();
        await Assert.That(live!.ReadingFont).IsEqualTo(DesktopReadingFont.Inter);
        settings.ReadingFont = DesktopReadingFont.Monospace;
        await settings.PendingSave;
        await Assert.That(settings.Problem).IsNull();
        await Assert.That(live!.ReadingFont).IsEqualTo(DesktopReadingFont.Monospace);
    });

    [Test]
    public Task Monospace_uses_the_embedded_face_and_falls_back_for_missing_script() => AvaloniaSession.RunOnUiAsync(async () => {
        var family = new FontFamily(DesktopReadingResources.MonospaceReadingFamily);
        var primary = new Typeface(family).GlyphTypeface;
        await Assert.That(primary.FamilyName).IsEqualTo("Spline Sans Mono");
        var map = primary.CharacterToGlyphMap;
        var ambiguous = "0OoIl1".Select(c => map.GetGlyph(c)).ToArray();
        await Assert.That(ambiguous.All(glyph => glyph != 0) && ambiguous.Distinct().Count() == ambiguous.Length).IsTrue();
        const char Cyrillic = 'Ж';
        await Assert.That(map.GetGlyph(Cyrillic)).IsEqualTo((ushort)0);
        await Assert.That(FontManager.Current.TryMatchCharacter(Cyrillic, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
            family, CultureInfo.InvariantCulture, out var fallback)).IsTrue();
        await Assert.That(fallback.GlyphTypeface.FamilyName).IsNotEqualTo(primary.FamilyName);
        await Assert.That(fallback.GlyphTypeface.CharacterToGlyphMap.GetGlyph(Cyrillic)).IsNotEqualTo((ushort)0);
    });

    [Test]
    public async Task Unreadable_state_opens_with_safe_defaults_and_the_next_save_recovers() {
        var store = new AppStateStore(Tmp.CreateFile("state.json", "{"));
        var initial = await store.LoadAsync();
        await Assert.That(DesktopAppearancePreferences.Read(initial.DesktopAppearanceByProfile?.GetValueOrDefault("profile")))
            .IsEqualTo(new DesktopAppearancePreferences());
        await Assert.That(await store.UpdateAsync(state => state with { UiScale = 1.25 })).IsTrue();
        await Assert.That((await store.LoadAsync()).UiScale).IsEqualTo(1.25);
    }
}
