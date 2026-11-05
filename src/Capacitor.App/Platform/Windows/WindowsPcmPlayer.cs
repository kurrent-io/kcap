using System.Runtime.Versioning;
using Capacitor.App.Services.Voice;
using NAudio.Wave;

namespace Capacitor.App.Platform.Windows;

/// waveOut playback of generated speech, one buffer at a time.
[SupportedOSPlatform("windows")]
internal sealed class WindowsPcmPlayer(TimeProvider time) : IPcmPlayer {
    readonly Lock _lock = new();
    WaveOutEvent? _current;
    // The headset link stays awake between the sentences of one reading, so only a sound after a
    // pause needs the lead-in silence.
    long _lastEnd;

    public async Task PlayAsync(float[] samples, int sampleRate, CancellationToken ct) {
        if (samples.Length == 0) return;
        var last = Interlocked.Read(ref _lastEnd);
        var awake = last != 0 && time.GetElapsedTime(last) < TimeSpan.FromSeconds(1);
        var lead = awake ? 0 : sampleRate * WindowsSpeechOutput.LeadMilliseconds / 1000;
        var pcm = new byte[(lead + samples.Length) * 2];
        for (var i = 0; i < samples.Length; i++) {
            var value = (short)Math.Clamp(samples[i] * short.MaxValue, short.MinValue, short.MaxValue);
            pcm[(lead + i) * 2] = (byte)value;
            pcm[(lead + i) * 2 + 1] = (byte)(value >> 8);
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(sampleRate, 16, 1));
        using var output = new WaveOutEvent();
        output.PlaybackStopped += (_, _) => done.TrySetResult();
        output.Init(stream);
        lock (_lock) _current = output;
        try {
            output.Play();
            await using (ct.Register(() => {
                try { output.Stop(); } catch { }
                done.TrySetResult();
            })) {
                await done.Task.ConfigureAwait(false);
            }
        } finally {
            Interlocked.Exchange(ref _lastEnd, time.GetTimestamp());
            lock (_lock) {
                if (ReferenceEquals(_current, output)) _current = null;
            }
        }
    }

    public void StopPlayback() {
        WaveOutEvent? current;
        lock (_lock) current = _current;
        try { current?.Stop(); } catch { }
    }

    public void Dispose() => StopPlayback();
}
