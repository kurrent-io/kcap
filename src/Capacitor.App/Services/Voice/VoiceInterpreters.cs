using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Services.Voice;

/// The interpreter behind each Understanding choice, made once for the app's lifetime so a
/// loaded model and its status survive switching back and forth.
public sealed class VoiceInterpreters : IDisposable {
    // No client timeout: a model download runs for minutes; each command brings its own limit.
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly OllamaVoiceInterpreter _local;
    readonly ClaudeVoiceInterpreter _online;
    readonly FallbackVoiceInterpreter _localThenOnline;

    public VoiceInterpreters(HarnessRegistry harnesses, TimeProvider time) {
        _local = new OllamaVoiceInterpreter(_http, OllamaVoiceInterpreter.DefaultUrl);
        _online = new ClaudeVoiceInterpreter(harnesses, time);
        _localThenOnline = new FallbackVoiceInterpreter(_local, _online);
    }

    public IVoiceInterpreter? For(VoiceUnderstanding understanding) => understanding switch {
        VoiceUnderstanding.Local => _localThenOnline,
        VoiceUnderstanding.Online => _online,
        _ => null,
    };

    public void Dispose() {
        _local.Dispose();
        _online.Dispose();
        _http.Dispose();
    }
}
