namespace Capacitor.App.Services.Voice;

/// Plays mono float samples on the default output device.
public interface IPcmPlayer : IDisposable {
    /// Completes when the samples have played, or when <see cref="StopPlayback"/> or the token cut them off.
    Task PlayAsync(float[] samples, int sampleRate, CancellationToken ct);

    void StopPlayback();
}
