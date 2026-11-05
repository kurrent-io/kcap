using System.Runtime.Versioning;
using System.Speech.Synthesis;
using Capacitor.App.Services.Voice;
using NAudio.Wave;

namespace Capacitor.App.Platform.Windows;

/// SAPI text-to-speech with the installed Windows voices, and generated tones for the cues.
[SupportedOSPlatform("windows")]
internal sealed class WindowsSpeechOutput : ISpeechOutput {
    static readonly TimeSpan CueLimit = TimeSpan.FromSeconds(2);

    /// Silence ahead of every sound. A Bluetooth headset wakes its audio link on the first samples
    /// it is sent and drops them, which clips a tone or the first syllable without this.
    public const int LeadMilliseconds = 120;
    const int TrailMilliseconds = 150;

    readonly SpeechSynthesizer _synth = new();
    readonly TimeProvider _time;
    readonly Lock _lock = new();
    readonly Dictionary<Prompt, TaskCompletionSource> _speaking = [];

    public WindowsSpeechOutput(TimeProvider time) {
        _time = time;
        _synth.SetOutputToDefaultAudioDevice();
        _synth.SpeakCompleted += (_, e) => {
            TaskCompletionSource? done;
            lock (_lock) {
                if (!_speaking.Remove(e.Prompt, out done)) return;
            }
            done.TrySetResult();
        };
        // Every installed voice, whatever the user's locale: an English voice on a German desktop is still a choice.
#pragma warning disable CA1304
        Voices = _synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
#pragma warning restore CA1304
    }

    public IReadOnlyList<string> Voices { get; }

    public void Configure(string? voice, int rate) {
        _synth.Rate = Math.Clamp(rate, VoicePreferences.MinRate, VoicePreferences.MaxRate);
        if (string.IsNullOrEmpty(voice) || !Voices.Contains(voice)) return;
        try { _synth.SelectVoice(voice); } catch (ArgumentException) { }
    }

    public async Task SpeakAsync(string text, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(text)) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new PromptBuilder();
        builder.AppendBreak(TimeSpan.FromMilliseconds(LeadMilliseconds));
        builder.AppendText(text);
        var prompt = new Prompt(builder);
        lock (_lock) _speaking[prompt] = done;
        _synth.SpeakAsync(prompt);
        await using (ct.Register(() => {
            try { _synth.SpeakAsyncCancel(prompt); } catch { }
            done.TrySetResult();
        })) {
            await done.Task.ConfigureAwait(false);
        }
    }

    public void StopSpeaking() {
        try { _synth.SpeakAsyncCancelAll(); } catch { }
    }

    public async Task PlayCueAsync(VoiceCue cue) {
        var (first, second) = cue switch {
            VoiceCue.Listening => (660.0, 990.0),
            VoiceCue.Accepted => (880.0, 0.0),
            VoiceCue.Ready => (1175.0, 0.0),
            _ => (440.0, 330.0),
        };
        var pcm = Tone(first, second);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(IMicrophone.SampleRate, 16, 1));
        using var output = new WaveOutEvent();
        output.PlaybackStopped += (_, _) => done.TrySetResult();
        output.Init(stream);
        output.Play();
        try {
            await done.Task.WaitAsync(CueLimit, _time, CancellationToken.None).ConfigureAwait(false);
        } catch (TimeoutException) {
        }
    }

    public IDisposable PlayWorking() {
        var stream = new RawSourceWaveStream(new MemoryStream(WorkingMusic.Value), new WaveFormat(IMicrophone.SampleRate, 16, 1));
        var output = new WaveOutEvent();
        output.Init(stream);
        output.Play();
        return System.Reactive.Disposables.Disposable.Create(() => {
            output.Stop();
            output.Dispose();
            stream.Dispose();
        });
    }

    // A slow, quiet pentatonic arpeggio, struck like a soft mallet, long enough to outlast the
    // interpreter's own time limit so it never needs to loop.
    static readonly Lazy<byte[]> WorkingMusic = new(() => {
        double[] notes = [523.25, 659.25, 783.99, 880.00, 783.99, 659.25, 587.33, 659.25];
        const double noteSeconds = 0.32;
        const double length = 32;
        var rate = IMicrophone.SampleRate;
        var samples = (int)(length * rate);
        var pcm = new byte[samples * 2];
        var noteSamples = (int)(noteSeconds * rate);
        for (var i = 0; i < samples; i++) {
            var note = i / noteSamples;
            var t = (double)(i % noteSamples) / rate;
            var frequency = notes[note % notes.Length];
            // A quick attack, then a decay; a fundamental with a quiet octave for a mallet-like tone.
            var envelope = Math.Min(1, t / 0.01) * Math.Exp(-t * 7);
            var fadeIn = Math.Min(1, (double)i / (rate * 0.6));
            var value = (Math.Sin(2 * Math.PI * frequency * t) + 0.25 * Math.Sin(4 * Math.PI * frequency * t)) * envelope * fadeIn * 0.07;
            var sample = (short)(value * short.MaxValue);
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    });

    // Two short sine notes with a soft attack and release, so the cue does not click.
    static byte[] Tone(double first, double second) {
        const int noteSamples = IMicrophone.SampleRate * 90 / 1000;
        const int lead = IMicrophone.SampleRate * LeadMilliseconds / 1000;
        const int trail = IMicrophone.SampleRate * TrailMilliseconds / 1000;
        var notes = second > 0 ? 2 : 1;
        var pcm = new byte[(lead + noteSamples * notes + trail) * 2];
        for (var n = 0; n < notes; n++) {
            var frequency = n == 0 ? first : second;
            for (var i = 0; i < noteSamples; i++) {
                var envelope = Math.Min(1, Math.Min(i, noteSamples - i) / (noteSamples * 0.2));
                var value = (short)(Math.Sin(2 * Math.PI * frequency * i / IMicrophone.SampleRate) * envelope * 0.25 * short.MaxValue);
                var offset = (lead + n * noteSamples + i) * 2;
                pcm[offset] = (byte)value;
                pcm[offset + 1] = (byte)(value >> 8);
            }
        }
        return pcm;
    }

    public void Dispose() {
        StopSpeaking();
        _synth.Dispose();
        TaskCompletionSource[] open;
        lock (_lock) {
            open = [.. _speaking.Values];
            _speaking.Clear();
        }
        foreach (var done in open) done.TrySetResult();
    }
}
