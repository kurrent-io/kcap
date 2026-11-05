using System.Text;
using Whisper.net;

namespace Capacitor.App.Services.Voice;

/// whisper.cpp over a local ggml model. One processor per utterance: the vocabulary prompt
/// changes with the wake word, and a processor carries context from one call into the next.
public sealed class WhisperRecognizer : ISpeechRecognizer {
    readonly WhisperFactory _factory;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly int _threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    public WhisperRecognizer(string modelPath) => _factory = WhisperFactory.FromPath(modelPath);

    public async Task<string> TranscribeAsync(float[] samples, string? vocabulary, CancellationToken ct) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            var builder = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithThreads(_threads)
                .WithNoContext()
                .WithSingleSegment();
            if (!string.IsNullOrWhiteSpace(vocabulary)) builder = builder.WithPrompt(vocabulary);
            await using var processor = builder.Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false)) text.Append(segment.Text);
            return text.ToString().Trim();
        } finally {
            _gate.Release();
        }
    }

    public void Dispose() {
        _factory.Dispose();
        _gate.Dispose();
    }
}
