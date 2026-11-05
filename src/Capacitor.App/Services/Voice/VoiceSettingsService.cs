using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.App.Services.Voice;

/// Voice preferences for this machine. A missing or unreadable file reads as the defaults, which
/// leave voice control off.
public sealed partial class VoiceSettingsService : IDisposable {
    readonly string _path;
    readonly BehaviorSubject<VoicePreferences> _changes;
    readonly SemaphoreSlim _gate = new(1, 1);

    public VoiceSettingsService(string path) {
        _path = path;
        _changes = new BehaviorSubject<VoicePreferences>(Read(path));
    }

    public VoicePreferences Current => _changes.Value;

    /// The current preferences, then each change.
    public IObservable<VoicePreferences> Changes => _changes;

    /// Applies at once; a failed write keeps the change for this run and returns false.
    public async Task<bool> SaveAsync(VoicePreferences preferences) {
        if (preferences == _changes.Value) return true;
        _changes.OnNext(preferences);
        await _gate.WaitAsync().ConfigureAwait(false);
        string? tmp = null;
        try {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            // The value current when the write runs, so overlapping saves cannot leave an older one on disk.
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_changes.Value, VoiceSettingsJsonContext.Default.VoicePreferences))
                .ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
            tmp = null;
            return true;
        } catch {
            return false;
        } finally {
            if (tmp is not null) {
                try { File.Delete(tmp); } catch { }
            }
            _gate.Release();
        }
    }

    public Task<bool> UpdateAsync(Func<VoicePreferences, VoicePreferences> mutate) => SaveAsync(mutate(_changes.Value));

    // Field by field, so a value this build cannot read (a model a later one added) costs only that field.
    internal static VoicePreferences Read(string path) {
        var defaults = new VoicePreferences();
        try {
            if (!File.Exists(path)) return defaults;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            return new VoicePreferences(
                Enabled: root.Bool("enabled") ?? defaults.Enabled,
                WakeWord: root.Str("wake_word") ?? defaults.WakeWord,
                Model: Enum.TryParse<VoiceModelSize>(root.Str("model"), ignoreCase: true, out var model) && Enum.IsDefined(model)
                    ? model : defaults.Model,
                ReadReplies: root.Bool("read_replies") ?? defaults.ReadReplies,
                Sensitivity: root.Prop("sensitivity") is { IsNumber: true } sensitivity
                    ? Math.Clamp(sensitivity.GetDouble(), 0, 1) : defaults.Sensitivity,
                VoiceName: root.Str("voice_name"),
                Rate: root.Num("rate") is { } rate
                    ? (int)Math.Clamp(rate, VoicePreferences.MinRate, VoicePreferences.MaxRate) : defaults.Rate,
                InstantAnswers: root.Bool("instant_answers") ?? defaults.InstantAnswers,
                Understanding: Enum.TryParse<VoiceUnderstanding>(root.Str("understanding"), ignoreCase: true, out var understanding)
                    && Enum.IsDefined(understanding) ? understanding : defaults.Understanding,
                Microphone: root.Str("microphone") is { Length: > 0 } microphone ? microphone : null,
                SaveAudio: root.Bool("save_audio") ?? defaults.SaveAudio);
        } catch {
            return defaults;
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(VoicePreferences))]
    partial class VoiceSettingsJsonContext : JsonSerializerContext;

    public void Dispose() {
        _changes.Dispose();
        _gate.Dispose();
    }
}
