namespace Capacitor.App.Services.Voice;

/// The default capture device, delivering 16 kHz mono samples in [-1, 1] on a capture thread.
public interface IMicrophone : IDisposable {
    const int SampleRate = 16_000;

    /// Throws when no capture device can be opened.
    void StartCapture(Action<float[]> samples);

    void StopCapture();
}
