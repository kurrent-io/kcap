namespace Capacitor.App.Services.Voice;

/// Reads a spoken command the fixed phrases did not match.
public interface IVoiceInterpreter {
    /// The commands meant, in order, or null when the interpreter could not answer; the caller
    /// then falls back to sending the words to the agent.
    Task<IReadOnlyList<VoiceCommand>?> InterpretAsync(string utterance, VoiceContext context, CancellationToken ct);

    /// What Settings shows about it: ready, loading, or what is missing.
    IObservable<string> Status { get; }

    /// Gets it ready ahead of the first command: a model download or load. Safe to call again.
    Task PrepareAsync(CancellationToken ct);
}
