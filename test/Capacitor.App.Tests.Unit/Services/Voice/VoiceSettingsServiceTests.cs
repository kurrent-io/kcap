using Capacitor.App.Services.Voice;

namespace Capacitor.App.Tests.Unit.Services.Voice;

public class VoiceSettingsServiceTests {
    [Test]
    public async Task Missing_or_corrupt_file_reads_as_voice_control_off() {
        using var tmp = TempDir.WithPathTo("voice.json", out var path);
        using (var missing = new VoiceSettingsService(path)) await Assert.That(missing.Current).IsEqualTo(new VoicePreferences());

        File.WriteAllText(path, "{not json");
        using var corrupt = new VoiceSettingsService(path);
        await Assert.That(corrupt.Current.Enabled).IsFalse();
        await Assert.That(corrupt.Current.WakeWord).IsEqualTo(VoicePreferences.DefaultWakeWord);
    }

    [Test]
    public async Task A_saved_change_is_what_the_next_run_reads() {
        using var tmp = TempDir.WithPathTo("voice.json", out var path);
        using (var service = new VoiceSettingsService(path)) {
            var saved = await service.UpdateAsync(p => p with {
                Enabled = true, WakeWord = "jarvis", Model = VoiceModelSize.SmallEn, Rate = 3, Understanding = VoiceUnderstanding.Local,
            });
            await Assert.That(saved).IsTrue();
        }

        using var reloaded = new VoiceSettingsService(path);
        await Assert.That(reloaded.Current).IsEqualTo(new VoicePreferences(Enabled: true, WakeWord: "jarvis", Model: VoiceModelSize.SmallEn, Rate: 3,
            Understanding: VoiceUnderstanding.Local));
    }

    [Test]
    public async Task An_unreadable_field_costs_only_that_field() {
        using var tmp = TempDir.WithPathTo("voice.json", out var path);
        File.WriteAllText(path, """{"enabled": true, "wake_word": "jarvis", "sensitivity": 7, "rate": -40, "model": "HugeEn"}""");

        using var service = new VoiceSettingsService(path);

        await Assert.That(service.Current).IsEqualTo(new VoicePreferences(
            Enabled: true, WakeWord: "jarvis", Model: VoiceModelSize.BaseEn, Sensitivity: 1.0, Rate: VoicePreferences.MinRate));
    }

    [Test]
    public async Task A_blank_wake_word_listens_for_the_default() =>
        await Assert.That(new VoicePreferences(WakeWord: "  ").EffectiveWakeWord).IsEqualTo(VoicePreferences.DefaultWakeWord);
}
