using System.Reactive.Subjects;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.App.Services.Voice;

/// Reads commands with Claude Haiku through <c>claude -p</c>, the headless path session titles
/// use, so it runs on the user's own Claude Code sign-in with no key of its own. Each command
/// starts the CLI, which costs a few seconds that a local model does not.
public sealed class ClaudeVoiceInterpreter(HarnessRegistry harnesses, TimeProvider time) : IVoiceInterpreter, IDisposable {
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    readonly BehaviorSubject<string> _status = new("Not started.");

    public IObservable<string> Status => _status;

    public Task PrepareAsync(CancellationToken ct) {
        _status.OnNext(harnesses.ResolveExecutable(HarnessId.Claude) is null
            ? "Claude Code isn't installed, so Claude Haiku can't be reached."
            : "Ready: Claude Haiku through Claude Code.");
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<VoiceCommand>?> InterpretAsync(string utterance, VoiceContext context, CancellationToken ct) {
        string? failure = null;
        var result = await ClaudeCliRunner.RunAsync(
            VoiceIntent.Message(utterance, context), Timeout, time, line => failure = line, profile: null, harnesses,
            model: "haiku", maxTurns: 3, promptViaStdin: true, jsonSchema: VoiceIntent.Schema,
            systemPrompt: VoiceIntent.SystemPrompt, ct: ct).ConfigureAwait(false);
        if (result is null) {
            _status.OnNext($"Claude Haiku didn't answer{(failure is null ? "." : $": {failure}")}");
            return null;
        }
        _status.OnNext("Ready: Claude Haiku through Claude Code.");
        return VoiceIntent.Read(result.Result, utterance, context);
    }

    public void Dispose() => _status.Dispose();
}
