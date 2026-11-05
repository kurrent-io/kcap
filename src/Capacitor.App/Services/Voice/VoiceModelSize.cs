using Whisper.net.Ggml;

namespace Capacitor.App.Services.Voice;

/// The local Whisper models. English-only builds: they are smaller and more accurate on quiet speech
/// than the multilingual ones of the same size. Measured on a desktop CPU, one utterance takes
/// about 0.6 s on Tiny, 1 s on Base and 4 s on Small.
public enum VoiceModelSize {
    TinyEn,
    BaseEn,
    SmallEn,
}

public static class VoiceModelSizeExtensions {
    public static GgmlType GgmlType(this VoiceModelSize size) => size switch {
        VoiceModelSize.TinyEn => Whisper.net.Ggml.GgmlType.TinyEn,
        VoiceModelSize.SmallEn => Whisper.net.Ggml.GgmlType.SmallEn,
        _ => Whisper.net.Ggml.GgmlType.BaseEn,
    };

    public static string FileName(this VoiceModelSize size) => size switch {
        VoiceModelSize.TinyEn => "ggml-tiny.en.bin",
        VoiceModelSize.SmallEn => "ggml-small.en.bin",
        _ => "ggml-base.en.bin",
    };

    public static string Label(this VoiceModelSize size) => size switch {
        VoiceModelSize.TinyEn => "Tiny (75 MB, fastest, least accurate)",
        VoiceModelSize.SmallEn => "Small (465 MB, most accurate, about 4 s per command)",
        _ => "Base (142 MB, about 1 s per command)",
    };
}
