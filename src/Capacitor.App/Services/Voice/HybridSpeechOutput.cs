using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Capacitor.App.Services.Voice;

/// Speaks with Kokoro when a Kokoro voice is chosen and the model is loaded, and with the system
/// voice otherwise, so speech never waits on the download. The model is loaded only once a Kokoro
/// voice is chosen: it holds hundreds of megabytes and its cores. With instant answers on, only
/// reading goes to Kokoro and short answers stay on the system voice, which starts at once. Kokoro is only a little faster than
/// real time, so text is generated a sentence ahead of the one playing: speech starts after the
/// first sentence rather than the whole reply.
public sealed partial class HybridSpeechOutput : ISpeechOutput {
    public const string SystemPrefix = "windows:";
    const int MinPiece = 30;
    // A longer first sentence is cut at a comma so speech starts after a few words are generated.
    const int FirstPieceTarget = 40;
    const int CacheSize = 6;

    readonly KokoroEngine _engine;
    readonly ISpeechOutput _system;
    readonly IPcmPlayer _player;
    readonly Lock _lock = new();
    KokoroVoice? _voice;
    float _speed = 1f;
    CancellationTokenSource? _speaking;
    bool _instantAnswers = true;
    readonly Dictionary<(string Text, int Voice, float Speed), float[]> _prepared = [];
    readonly Queue<(string Text, int Voice, float Speed)> _preparedOrder = new();

    public HybridSpeechOutput(KokoroEngine engine, ISpeechOutput system, IPcmPlayer player) {
        _engine = engine;
        _system = system;
        _player = player;
    }

    /// Kokoro's voices, then the system's, as the keys <see cref="VoicePreferences.VoiceName"/> stores.
    public IReadOnlyList<string> Voices =>
        [.. KokoroVoice.English.Select(v => v.StoredKey), .. _system.Voices.Select(name => SystemPrefix + name)];

    public void Configure(string? voice, int rate) {
        var kokoro = KokoroVoice.FromKey(voice);
        lock (_lock) {
            _voice = kokoro;
            _speed = 1f + Math.Clamp(rate, VoicePreferences.MinRate, VoicePreferences.MaxRate) * 0.05f;
        }
        // A bare name is a system voice saved before Kokoro voices existed.
        var systemVoice = kokoro is null && voice is not null && voice.StartsWith(SystemPrefix, StringComparison.Ordinal)
            ? voice[SystemPrefix.Length..] : kokoro is null ? voice : null;
        _system.Configure(systemVoice, rate);
        if (kokoro is not null) _ = _engine.EnsureLoadedAsync();
    }

    public bool InstantAnswers {
        get { lock (_lock) return _instantAnswers; }
        set { lock (_lock) _instantAnswers = value; }
    }

    public Task SpeakAsync(string text, CancellationToken ct) {
        bool instant;
        lock (_lock) instant = _instantAnswers;
        return instant ? _system.SpeakAsync(text, ct) : ReadAsync(text, ct);
    }

    public void Prepare(string text) {
        KokoroVoice? voice;
        float speed;
        lock (_lock) {
            voice = _voice;
            speed = _speed;
        }
        if (voice is null || _engine.Ready is not { } synthesizer || Pieces(text) is not [var first, ..]) return;
        var key = (first, voice.Id, speed);
        lock (_lock) {
            if (_prepared.ContainsKey(key)) return;
        }
        _ = Task.Run(() => {
            try {
                var samples = synthesizer.Generate(first, voice, speed);
                lock (_lock) Remember(key, samples);
            } catch {
            }
        });
    }

    void Remember((string, int, float) key, float[] samples) {
        if (_prepared.TryAdd(key, samples)) _preparedOrder.Enqueue(key);
        while (_preparedOrder.Count > CacheSize) _prepared.Remove(_preparedOrder.Dequeue());
    }

    float[] Generate(KokoroSynthesizer synthesizer, string piece, KokoroVoice voice, float speed) {
        lock (_lock) {
            if (_prepared.Remove((piece, voice.Id, speed), out var ready)) return ready;
        }
        return synthesizer.Generate(piece, voice, speed);
    }

    public async Task ReadAsync(string text, CancellationToken ct) {
        KokoroVoice? voice;
        float speed;
        lock (_lock) {
            voice = _voice;
            speed = _speed;
        }
        if (voice is null || _engine.Ready is not { } synthesizer) {
            await _system.SpeakAsync(text, ct).ConfigureAwait(false);
            return;
        }

        using var speaking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lock) _speaking = speaking;
        var pieces = Channel.CreateBounded<float[]>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        var generate = Task.Run(() => {
            try {
                foreach (var piece in Pieces(text)) {
                    speaking.Token.ThrowIfCancellationRequested();
                    var samples = Generate(synthesizer, piece, voice, speed);
                    pieces.Writer.WriteAsync(samples, speaking.Token).AsTask().GetAwaiter().GetResult();
                }
                pieces.Writer.TryComplete();
            } catch (Exception ex) {
                pieces.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);
        try {
            await foreach (var samples in pieces.Reader.ReadAllAsync(speaking.Token).ConfigureAwait(false))
                await _player.PlayAsync(samples, synthesizer.SampleRate, speaking.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception) {
            // Kokoro failed part-way: say it with the system voice rather than leave it unsaid.
            if (!speaking.IsCancellationRequested) await _system.SpeakAsync(text, ct).ConfigureAwait(false);
        } finally {
            speaking.Cancel();
            lock (_lock) {
                if (ReferenceEquals(_speaking, speaking)) _speaking = null;
            }
            try { await generate.ConfigureAwait(false); } catch { }
        }
    }

    public void StopSpeaking() {
        CancellationTokenSource? speaking;
        lock (_lock) speaking = _speaking;
        try { speaking?.Cancel(); } catch (ObjectDisposedException) { }
        _player.StopPlayback();
        _system.StopSpeaking();
    }

    public Task PlayCueAsync(VoiceCue cue) => _system.PlayCueAsync(cue);

    public IDisposable PlayWorking() => _system.PlayWorking();

    /// The pieces generated one after another: sentences, with a long first one cut at its first
    /// comma so playback starts sooner.
    internal static IReadOnlyList<string> Pieces(string text) {
        // The first sentence stands alone however short: joining it would only delay the start.
        var raw = SentenceBreak().Split(text.Trim()).Where(s => s.Length > 0).ToList();
        if (raw.Count == 0) return [];
        var pieces = new List<string>();
        var first = raw[0];
        var comma = first.Length > FirstPieceTarget ? first.IndexOf(", ", 12, StringComparison.Ordinal) : -1;
        if (comma > 0 && comma < first.Length - 10) {
            pieces.Add(first[..(comma + 1)]);
            raw[0] = first[(comma + 2)..];
        } else {
            pieces.Add(first);
            raw.RemoveAt(0);
        }
        pieces.AddRange(Sentences(string.Join(' ', raw)));
        return pieces;
    }

    /// Sentences, with short ones joined to the next: each generation call has a fixed cost.
    internal static IReadOnlyList<string> Sentences(string text) {
        var pieces = new List<string>();
        var pending = "";
        foreach (var sentence in SentenceBreak().Split(text.Trim())) {
            if (sentence.Length == 0) continue;
            pending = pending.Length == 0 ? sentence : $"{pending} {sentence}";
            if (pending.Length < MinPiece) continue;
            pieces.Add(pending);
            pending = "";
        }
        if (pending.Length > 0) pieces.Add(pending);
        return pieces;
    }

    [GeneratedRegex(@"(?<=[.!?…:;])\s+")]
    private static partial Regex SentenceBreak();

    public void Dispose() {
        StopSpeaking();
        _player.Dispose();
        _system.Dispose();
    }
}
