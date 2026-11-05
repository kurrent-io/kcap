namespace Capacitor.App.Services.Voice;

/// Spoken feedback and the short tones that mark listening.
public interface ISpeechOutput : IDisposable {
    /// Installed voices, by name.
    IReadOnlyList<string> Voices { get; }

    void Configure(string? voice, int rate);

    /// Completes when the text has been spoken or <see cref="StopSpeaking"/> cut it off.
    Task SpeakAsync(string text, CancellationToken ct);

    void StopSpeaking();

    /// Reads out a reply, where a slower, more natural voice is worth its start-up time.
    Task ReadAsync(string text, CancellationToken ct) => SpeakAsync(text, ct);

    /// Text likely to be read next, which an output may start preparing now.
    void Prepare(string text) { }

    /// Whether short answers keep the instant voice when a slower one is chosen for reading.
    bool InstantAnswers { get => true; set { } }

    Task PlayCueAsync(VoiceCue cue);

    /// Starts soft music that plays until the returned handle is disposed, while a request is worked on.
    IDisposable PlayWorking() => System.Reactive.Disposables.Disposable.Empty;
}
