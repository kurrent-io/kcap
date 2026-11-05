using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services.Voice;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Settings' Voice tab. Each switch saves on change; the wake word saves once typing pauses, so
/// voice control never listens for half a word.
public sealed class VoiceSettingsViewModel : ReactiveObject, IDisposable {
    static readonly TimeSpan WakeWordPause = TimeSpan.FromMilliseconds(700);

    readonly VoiceSettingsService _settings;
    readonly CompositeDisposable _subscriptions = new();
    readonly SerialDisposable _understandingStatus = new();
    readonly Func<VoiceUnderstanding, IVoiceInterpreter?> _interpreters;
    VoiceUnderstandingOption _understanding;
    VoiceChoice _microphone;
    string? _understandingStatusText;
    bool _enabled;
    string _wakeWord;
    VoiceModelOption _model;
    bool _readReplies;
    bool _instantAnswers;
    bool _saveAudio;
    string? _timingsText;
    double _sensitivity;
    VoiceChoice _voice;
    string? _naturalVoiceStatus;
    int _rate;
    string _statusText = VoiceStatus.Off.Detail;
    string? _lastHeard;
    bool _failed;
    string? _saveMessage;

    public VoiceSettingsViewModel(VoiceSettingsService settings, VoiceAssistant? assistant, IObservable<string?>? naturalVoiceStatus = null,
            Func<VoiceUnderstanding, IVoiceInterpreter?>? interpreters = null, Func<IReadOnlyList<string>>? microphones = null,
            Func<string?>? defaultMicrophone = null) {
        _settings = settings;
        _interpreters = interpreters ?? (_ => null);
        _understandingStatus.DisposeWith(_subscriptions);
        var current = settings.Current;
        _enabled = current.Enabled;
        _wakeWord = current.WakeWord;
        _model = Models.First(m => m.Size == current.Model);
        _readReplies = current.ReadReplies;
        _instantAnswers = current.InstantAnswers;
        _saveAudio = current.SaveAudio;
        RecordingsFolder = assistant?.RecordingsDirectory;
        _understanding = Understandings.First(u => u.Value == current.Understanding);
        Microphones = MicrophoneChoices(microphones, defaultMicrophone, current.Microphone);
        _microphone = Microphones.First(m => m.Key == (current.Microphone ?? ""));
        WatchUnderstanding(current.Understanding);
        _sensitivity = Math.Round(current.Sensitivity * 10) * 10;
        Voices = Choices([]);
        _voice = ChoiceFor(current.VoiceName);
        _rate = current.Rate;
        IsAvailable = assistant is not null;

        var sensitivity = this.WhenAnyValue(x => x.Sensitivity);
        var rate = this.WhenAnyValue(x => x.Rate);
        SensitivityDownCommand = ReactiveCommand.Create(() => { Sensitivity = Math.Max(0, Sensitivity - 10); }, sensitivity.Select(s => s > 0))
            .DisposeWith(_subscriptions);
        SensitivityUpCommand = ReactiveCommand.Create(() => { Sensitivity = Math.Min(100, Sensitivity + 10); }, sensitivity.Select(s => s < 100))
            .DisposeWith(_subscriptions);
        RateDownCommand = ReactiveCommand.Create(() => { Rate--; }, rate.Select(r => r > VoicePreferences.MinRate)).DisposeWith(_subscriptions);
        RateUpCommand = ReactiveCommand.Create(() => { Rate++; }, rate.Select(r => r < VoicePreferences.MaxRate)).DisposeWith(_subscriptions);

        this.WhenAnyValue(x => x.WakeWord).Skip(1).Throttle(WakeWordPause).ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(word => Save(p => p with { WakeWord = word.Trim() })).DisposeWith(_subscriptions);
        this.WhenAnyValue(x => x.Sensitivity).Skip(1).Throttle(TimeSpan.FromMilliseconds(250)).ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(value => Save(p => p with { Sensitivity = Math.Clamp(value / 100, 0, 1) })).DisposeWith(_subscriptions);
        this.WhenAnyValue(x => x.Rate).Skip(1).Throttle(TimeSpan.FromMilliseconds(250)).ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(value => Save(p => p with { Rate = value })).DisposeWith(_subscriptions);

        if (assistant is not null) {
            assistant.Status.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(status => {
                StatusText = status.Detail;
                LastHeard = status.LastHeard;
                Failed = status.State == VoiceState.Failed;
                if (assistant.Voices.Any(k => k.StartsWith(HybridSpeechOutput.SystemPrefix, StringComparison.Ordinal))
                    && Voices.Count == KokoroVoice.English.Count + 1) {
                    var key = _voice.Key;
                    Voices = Choices(assistant.Voices);
                    this.RaisePropertyChanged(nameof(Voices));
                    this.RaiseAndSetIfChanged(ref _voice, Voices.FirstOrDefault(v => v.Key == key) ?? _voice, nameof(Voice));
                }
            }).DisposeWith(_subscriptions);
        }
        assistant?.Timings.ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(text => TimingsText = text).DisposeWith(_subscriptions);
        naturalVoiceStatus?.ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(text => NaturalVoiceStatus = text).DisposeWith(_subscriptions);
    }

    public bool IsAvailable { get; }

    public string UnavailableText => "Voice control is available in the Windows app.";

    public IReadOnlyList<VoiceModelOption> Models { get; } =
        Enum.GetValues<VoiceModelSize>().Select(size => new VoiceModelOption(size, size.Label())).ToList();

    /// The Windows voices (once speech output has listed them), then Kokoro's.
    public IReadOnlyList<VoiceChoice> Voices { get; private set; }

    /// The natural voice's download and load progress, or why it is not in use.
    public string? NaturalVoiceStatus {
        get => _naturalVoiceStatus;
        private set => this.RaiseAndSetIfChanged(ref _naturalVoiceStatus, value);
    }

    const string WindowsDefault = HybridSpeechOutput.SystemPrefix;

    static IReadOnlyList<VoiceChoice> Choices(IReadOnlyList<string> spoken) => [
        new VoiceChoice(WindowsDefault, "Windows default voice · instant"),
        .. spoken.Where(k => k.StartsWith(HybridSpeechOutput.SystemPrefix, StringComparison.Ordinal) && k != WindowsDefault)
            .Select(k => new VoiceChoice(k, $"{k[HybridSpeechOutput.SystemPrefix.Length..]} · Windows")),
        .. KokoroVoice.English.Select(v => new VoiceChoice(v.StoredKey, $"{v.Label} · natural, slower")),
    ];

    VoiceChoice ChoiceFor(string? stored) {
        var key = stored switch {
            null or "" => WindowsDefault,
            _ when stored.StartsWith(KokoroVoice.KeyPrefix, StringComparison.Ordinal) => stored,
            _ when stored.StartsWith(HybridSpeechOutput.SystemPrefix, StringComparison.Ordinal) => stored,
            // A Windows voice saved before Kokoro voices existed carries no prefix.
            _ => HybridSpeechOutput.SystemPrefix + stored,
        };
        return Voices.FirstOrDefault(v => v.Key == key) ?? new VoiceChoice(key, key.StartsWith(HybridSpeechOutput.SystemPrefix, StringComparison.Ordinal)
            ? $"{key[HybridSpeechOutput.SystemPrefix.Length..]} · Windows" : key);
    }

    public string CommandsText => VoiceCommandExecutor.HelpText;

    public bool Enabled {
        get => _enabled;
        set {
            if (value == _enabled) return;
            this.RaiseAndSetIfChanged(ref _enabled, value);
            Save(p => p with { Enabled = value });
        }
    }

    public string WakeWord {
        get => _wakeWord;
        set {
            this.RaiseAndSetIfChanged(ref _wakeWord, value);
            this.RaisePropertyChanged(nameof(WakeWordHint));
        }
    }

    public string WakeWordHint => string.IsNullOrWhiteSpace(WakeWord)
        ? $"Blank listens for “{VoicePreferences.DefaultWakeWord}”."
        : $"Say “{WakeWord.Trim()}”, then your prompt or a command — in one breath, or after the tone.";

    /// Windows' default device first; a saved device that is unplugged stays listed, so the choice is not lost.
    public IReadOnlyList<VoiceChoice> Microphones { get; }

    public VoiceChoice Microphone {
        get => _microphone;
        set {
            if (value is null || value.Key == _microphone.Key) return;
            this.RaiseAndSetIfChanged(ref _microphone, value);
            Save(p => p with { Microphone = value.Key.Length > 0 ? value.Key : null });
        }
    }

    static IReadOnlyList<VoiceChoice> MicrophoneChoices(Func<IReadOnlyList<string>>? list, Func<string?>? defaultName, string? saved) {
        IReadOnlyList<string> names;
        string? system;
        try {
            names = list?.Invoke() ?? [];
            system = defaultName?.Invoke();
        } catch (Exception) {
            names = [];
            system = null;
        }
        var choices = new List<VoiceChoice> { new("", system is null ? "Windows default microphone" : $"Windows default · {system}") };
        choices.AddRange(names.Distinct(StringComparer.Ordinal).Select(n => new VoiceChoice(n, n)));
        if (saved is { Length: > 0 } && !names.Contains(saved, StringComparer.Ordinal)) choices.Add(new VoiceChoice(saved, $"{saved} · not connected"));
        return choices;
    }

    public IReadOnlyList<VoiceUnderstandingOption> Understandings { get; } = [
        new(VoiceUnderstanding.Phrases, "Fixed phrases only · anything else goes to the agent"),
        new(VoiceUnderstanding.Local, $"Local model · {OllamaVoiceInterpreter.DefaultModel} on Ollama, Claude Haiku when unsure"),
        new(VoiceUnderstanding.Online, "Claude Haiku · online, through Claude Code"),
    ];

    public VoiceUnderstandingOption Understanding {
        get => _understanding;
        set {
            if (value is null || value == _understanding) return;
            this.RaiseAndSetIfChanged(ref _understanding, value);
            WatchUnderstanding(value.Value);
            Save(p => p with { Understanding = value.Value });
        }
    }

    /// The chosen interpreter's download, load or sign-in state.
    public string? UnderstandingStatus {
        get => _understandingStatusText;
        private set => this.RaiseAndSetIfChanged(ref _understandingStatusText, value);
    }

    void WatchUnderstanding(VoiceUnderstanding understanding) {
        UnderstandingStatus = null;
        if (_interpreters(understanding) is not { } interpreter) {
            _understandingStatus.Disposable = null;
            return;
        }
        _understandingStatus.Disposable = interpreter.Status.ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(text => UnderstandingStatus = text);
        _ = interpreter.PrepareAsync(CancellationToken.None);
    }

    public VoiceModelOption Model {
        get => _model;
        set {
            if (value is null || value == _model) return;
            this.RaiseAndSetIfChanged(ref _model, value);
            Save(p => p with { Model = value.Size });
        }
    }

    /// Each command kept as a WAV with what the models heard, to compare models and microphones.
    public bool SaveAudio {
        get => _saveAudio;
        set {
            if (value == _saveAudio) return;
            this.RaiseAndSetIfChanged(ref _saveAudio, value);
            Save(p => p with { SaveAudio = value });
        }
    }

    public string? RecordingsFolder { get; }

    public string SaveAudioHint => RecordingsFolder is { } folder
        ? $"Only commands, never other talk in the room. Saved in {folder}."
        : "Only commands, never other talk in the room.";

    /// How long the last command took, stage by stage.
    public string? TimingsText {
        get => _timingsText;
        private set => this.RaiseAndSetIfChanged(ref _timingsText, value);
    }

    /// Short answers in the instant Windows voice, with the natural voice kept for reading replies.
    public bool InstantAnswers {
        get => _instantAnswers;
        set {
            if (value == _instantAnswers) return;
            this.RaiseAndSetIfChanged(ref _instantAnswers, value);
            Save(p => p with { InstantAnswers = value });
        }
    }

    public bool ReadReplies {
        get => _readReplies;
        set {
            if (value == _readReplies) return;
            this.RaiseAndSetIfChanged(ref _readReplies, value);
            Save(p => p with { ReadReplies = value });
        }
    }

    /// 0–100, in steps of ten.
    public double Sensitivity {
        get => _sensitivity;
        private set {
            this.RaiseAndSetIfChanged(ref _sensitivity, value);
            this.RaisePropertyChanged(nameof(SensitivityLabel));
        }
    }

    public string SensitivityLabel => $"{Sensitivity:0}%";

    public ReactiveCommand<Unit, Unit> SensitivityDownCommand { get; }
    public ReactiveCommand<Unit, Unit> SensitivityUpCommand { get; }

    public VoiceChoice Voice {
        get => _voice;
        set {
            if (value is null || value.Key == _voice.Key) return;
            this.RaiseAndSetIfChanged(ref _voice, value);
            Save(p => p with { VoiceName = value.Key });
        }
    }

    public int Rate {
        get => _rate;
        private set {
            this.RaiseAndSetIfChanged(ref _rate, Math.Clamp(value, VoicePreferences.MinRate, VoicePreferences.MaxRate));
            this.RaisePropertyChanged(nameof(RateLabel));
        }
    }

    public string RateLabel => Rate switch { 0 => "Normal", > 0 => $"+{Rate}", _ => $"{Rate}" };

    public ReactiveCommand<Unit, Unit> RateDownCommand { get; }
    public ReactiveCommand<Unit, Unit> RateUpCommand { get; }

    public string StatusText {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public string? LastHeard {
        get => _lastHeard;
        private set {
            this.RaiseAndSetIfChanged(ref _lastHeard, value);
            this.RaisePropertyChanged(nameof(LastHeardText));
        }
    }

    public string? LastHeardText => LastHeard is { Length: > 0 } heard ? $"Last heard: “{heard}”" : null;

    public bool Failed {
        get => _failed;
        private set => this.RaiseAndSetIfChanged(ref _failed, value);
    }

    public string? SaveMessage {
        get => _saveMessage;
        private set => this.RaiseAndSetIfChanged(ref _saveMessage, value);
    }

    void Save(Func<VoicePreferences, VoicePreferences> change) => _ = SaveAsync(change);

    async Task SaveAsync(Func<VoicePreferences, VoicePreferences> change) {
        var saved = false;
        try {
            saved = await _settings.UpdateAsync(change);
        } catch {
        }
        SaveMessage = saved ? null : "Could not save voice settings. The change still applies for this run.";
    }

    public void Dispose() => _subscriptions.Dispose();
}
