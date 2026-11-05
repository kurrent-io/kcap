namespace Capacitor.App.Services.Voice;

/// One of Kokoro's English speakers. The id is its position in the model's voices file.
public sealed record KokoroVoice(int Id, string Key, string Label) {
    public const string KeyPrefix = "kokoro:";
    /// The Kokoro voice offered first. Not the app's default: the Windows voice starts at once,
    /// where Kokoro takes about a second per sentence.
    public const string DefaultKey = KeyPrefix + "af_heart";

    // The order the v1.0 voices file was built in; ids past these are other languages.
    public static IReadOnlyList<KokoroVoice> English { get; } = [
        new(0, "af_alloy", "Alloy (American, female)"),
        new(1, "af_aoede", "Aoede (American, female)"),
        new(2, "af_bella", "Bella (American, female)"),
        new(3, "af_heart", "Heart (American, female)"),
        new(4, "af_jessica", "Jessica (American, female)"),
        new(5, "af_kore", "Kore (American, female)"),
        new(6, "af_nicole", "Nicole (American, female, soft)"),
        new(7, "af_nova", "Nova (American, female)"),
        new(8, "af_river", "River (American, female)"),
        new(9, "af_sarah", "Sarah (American, female)"),
        new(10, "af_sky", "Sky (American, female)"),
        new(11, "am_adam", "Adam (American, male)"),
        new(12, "am_echo", "Echo (American, male)"),
        new(13, "am_eric", "Eric (American, male)"),
        new(14, "am_fenrir", "Fenrir (American, male)"),
        new(15, "am_liam", "Liam (American, male)"),
        new(16, "am_michael", "Michael (American, male)"),
        new(17, "am_onyx", "Onyx (American, male)"),
        new(18, "am_puck", "Puck (American, male)"),
        new(19, "am_santa", "Santa (American, male)"),
        new(20, "bf_alice", "Alice (British, female)"),
        new(21, "bf_emma", "Emma (British, female)"),
        new(22, "bf_isabella", "Isabella (British, female)"),
        new(23, "bf_lily", "Lily (British, female)"),
        new(24, "bm_daniel", "Daniel (British, male)"),
        new(25, "bm_fable", "Fable (British, male)"),
        new(26, "bm_george", "George (British, male)"),
        new(27, "bm_lewis", "Lewis (British, male)"),
    ];

    /// The Kokoro voice a stored voice name means, or null for a Windows voice, which is also what
    /// no stored name means.
    public static KokoroVoice? FromKey(string? key) {
        if (key is null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return null;
        var name = key[KeyPrefix.Length..];
        return English.FirstOrDefault(v => v.Key == name) ?? English.First(v => KeyPrefix + v.Key == DefaultKey);
    }

    public string StoredKey => KeyPrefix + Key;
}
