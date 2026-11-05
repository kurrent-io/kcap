namespace Capacitor.App.Services.Voice;

/// Asks a fast interpreter first and a slower, smarter one only when the first has no answer:
/// a small local model that cannot read the words, or an Ollama that is not running.
public sealed class FallbackVoiceInterpreter(IVoiceInterpreter first, IVoiceInterpreter then) : IVoiceInterpreter {
    public IObservable<string> Status => first.Status;

    public async Task PrepareAsync(CancellationToken ct) {
        await then.PrepareAsync(ct).ConfigureAwait(false);
        await first.PrepareAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VoiceCommand>?> InterpretAsync(string utterance, VoiceContext context, CancellationToken ct) =>
        await first.InterpretAsync(utterance, context, ct).ConfigureAwait(false)
        ?? await then.InterpretAsync(utterance, context, ct).ConfigureAwait(false);
}
