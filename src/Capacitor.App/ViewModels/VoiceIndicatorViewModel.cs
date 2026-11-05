using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Capacitor.App.Services.Voice;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The rail footer's record button: a circle that starts listening, a square that stops it, and
/// a line saying what voice control is doing.
public sealed class VoiceIndicatorViewModel : ReactiveObject, IDisposable {
    const double GlyphSize = 12;
    const double HaloReach = 14;
    // Falls back by this share of the gap each buffer, so the halo eases down between words
    // instead of flickering with every syllable.
    const double Decay = 0.75;

    readonly IDisposable _subscription;
    readonly IDisposable? _levelSubscription;
    VoiceStatus _status = VoiceStatus.Off;
    double _level;

    public VoiceIndicatorViewModel(IObservable<VoiceStatus> status, VoiceSettingsService settings,
            IObservable<double>? levels = null, IScheduler? scheduler = null) {
        var ui = scheduler ?? RxSchedulers.MainThreadScheduler;
        _levelSubscription = levels?.ObserveOn(ui).Subscribe(level => {
            _level = level >= _level ? level : _level * Decay + level * (1 - Decay);
            if (_level < 0.01) _level = 0;
            this.RaisePropertyChanged(nameof(HaloSize));
        });
        _subscription = status.ObserveOn(ui).Subscribe(next => {
            _status = next;
            this.RaisePropertyChanged(nameof(Text));
            this.RaisePropertyChanged(nameof(Tip));
            this.RaisePropertyChanged(nameof(ActionName));
            this.RaisePropertyChanged(nameof(IsAttending));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(IsOn));
            this.RaisePropertyChanged(nameof(HaloSize));
            this.RaisePropertyChanged(nameof(HeardText));
        });
        ToggleCommand = ReactiveCommand.CreateFromTask(() => ToggleAsync(settings));
    }

    /// Listening, or getting ready to: the button shows the square.
    public bool IsOn => _status.IsActive;

    /// Heard the wake word, or waiting on a follow-up: the next words are a command.
    public bool IsAttending => _status.State is VoiceState.AwaitingCommand or VoiceState.AwaitingAnswer;

    public bool IsFailed => _status.State == VoiceState.Failed;

    public string Text => _status.State switch {
        VoiceState.Preparing => "Starting…",
        VoiceState.Listening => $"Say “{_status.WakeWord}”…",
        VoiceState.AwaitingCommand => "Go ahead",
        VoiceState.AwaitingAnswer => "Waiting for you",
        VoiceState.Working => "Working…",
        VoiceState.Speaking => "Speaking",
        VoiceState.Failed => "Voice stopped",
        _ => "Voice",
    };

    /// The halo behind the button, sized by how loud the microphone is: it moves as you speak.
    /// Collapsed to the glyph when not listening.
    public double HaloSize => IsOn ? GlyphSize + HaloReach * _level : 0;

    /// What the last utterance was heard as, and why nothing happened when it was ignored: the
    /// only sign that speech without the wake word reached the app at all.
    public string? HeardText => !IsOn || _status.LastHeard is not { Length: > 0 } heard ? null
        : _status.MissedWakeWord ? $"Heard “{Clip(heard)}” · say “{_status.WakeWord}” first"
        : $"Heard “{Clip(heard)}”";

    static string Clip(string text) => text.Length <= 60 ? text : text[..60].TrimEnd() + "…";

    public string ActionName => IsOn ? "Stop listening" : "Start listening";

    public string Tip {
        get {
            var tip = _status.State == VoiceState.Speaking ? "Speaking." : _status.Detail;
            if (_status.LastHeard is { Length: > 0 } heard) tip += $"\nLast heard: “{heard}”";
            return $"{ActionName}\n{tip}";
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    // A run that failed is still switched on, so starting again switches it off and back on: the
    // assistant starts a fresh run only on a change.
    async Task ToggleAsync(VoiceSettingsService settings) {
        if (IsOn) {
            await settings.UpdateAsync(p => p with { Enabled = false });
            return;
        }
        if (settings.Current.Enabled) await settings.UpdateAsync(p => p with { Enabled = false });
        await settings.UpdateAsync(p => p with { Enabled = true });
    }

    public void Dispose() {
        _subscription.Dispose();
        _levelSubscription?.Dispose();
        ToggleCommand.Dispose();
    }
}
