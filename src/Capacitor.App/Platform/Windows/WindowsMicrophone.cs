using System.Runtime.Versioning;
using Capacitor.App.Services.Voice;
using NAudio.Wave;

namespace Capacitor.App.Platform.Windows;

/// A Windows capture device through waveIn, resampled by the driver to 16 kHz mono: the one named,
/// or the system default when it is not named or not plugged in.
[SupportedOSPlatform("windows")]
internal sealed class WindowsMicrophone(string? name) : IMicrophone {
    // waveIn's own number for whatever Windows has set as the default recording device.
    const int SystemDefault = -1;

    WaveInEvent? _wave;

    /// The capture devices by name. waveIn cuts names to 31 characters, which is also how they are matched.
    public static IReadOnlyList<string> Names() =>
        Enumerable.Range(0, WaveInEvent.DeviceCount).Select(i => WaveInEvent.GetCapabilities(i).ProductName).ToList();

    /// What Windows has set as the default recording device. Windows keeps a second default for
    /// calls, often a headset, which is not the one waveIn opens.
    public static string? DefaultName() {
        try {
            using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console);
            return device.FriendlyName;
        } catch (Exception) {
            return null;
        }
    }

    static int DeviceFor(string? name) {
        if (name is null) return SystemDefault;
        for (var i = 0; i < WaveInEvent.DeviceCount; i++) {
            if (WaveInEvent.GetCapabilities(i).ProductName == name) return i;
        }
        return SystemDefault;
    }

    public void StartCapture(Action<float[]> samples) {
        if (_wave is not null) return;
        if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException("No microphone is connected.");
        var wave = new WaveInEvent {
            DeviceNumber = DeviceFor(name),
            WaveFormat = new WaveFormat(IMicrophone.SampleRate, 16, 1),
            BufferMilliseconds = 60,
            NumberOfBuffers = 4,
        };
        wave.DataAvailable += (_, e) => {
            var count = e.BytesRecorded / 2;
            var frame = new float[count];
            for (var i = 0; i < count; i++) frame[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
            samples(frame);
        };
        wave.StartRecording();
        _wave = wave;
    }

    public void StopCapture() {
        var wave = _wave;
        _wave = null;
        if (wave is null) return;
        try { wave.StopRecording(); } catch { }
        wave.Dispose();
    }

    public void Dispose() => StopCapture();
}
