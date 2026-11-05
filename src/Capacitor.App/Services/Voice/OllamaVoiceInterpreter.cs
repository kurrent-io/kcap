using System.Globalization;
using System.Net.Http.Headers;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;

namespace Capacitor.App.Services.Voice;

/// Reads commands with a small model served by a local Ollama, the one the local edition runs
/// for search. The answer is held to <see cref="VoiceIntent.Schema"/> by Ollama itself, so the
/// model cannot reply with anything but actions.
public sealed class OllamaVoiceInterpreter(HttpClient http, Uri baseUrl, string model = OllamaVoiceInterpreter.DefaultModel)
        : IVoiceInterpreter, IDisposable {
    // With the worked examples in the instructions, 3B was right 20 times in 25 at about 0.25 s;
    // 1.5B was right 11 times.
    public const string DefaultModel = "qwen2.5:3b";
    public static readonly Uri DefaultUrl = new("http://localhost:11434");
    const string KeepAlive = "30m";
    // The prompt is about a thousand tokens. A model's own default can be hundreds of thousands,
    // which pushes most of it off the GPU and makes each answer take seconds.
    const int ContextTokens = 4096;

    readonly BehaviorSubject<string> _status = new("Not started.");
    readonly SemaphoreSlim _prepare = new(1, 1);
    volatile bool _ready;

    public IObservable<string> Status => _status;

    public async Task PrepareAsync(CancellationToken ct) {
        if (_ready) return;
        await _prepare.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_ready) return;
            _status.OnNext("Checking Ollama…");
            if (!await RespondsAsync(ct).ConfigureAwait(false)) {
                _status.OnNext("Ollama isn't running. Start Ollama, then pick this again.");
                return;
            }
            if (!await HasModelAsync(ct).ConfigureAwait(false)) await PullAsync(ct).ConfigureAwait(false);
            _status.OnNext($"Loading {model}…");
            using (var warm = Post("api/generate", json => {
                       json.WriteString("model", model);
                       json.WriteString("keep_alive", KeepAlive);
                       json.WriteStartObject("options");
                       json.WriteNumber("num_ctx", ContextTokens);
                       json.WriteEndObject();
                   }))
            using (var response = await http.SendAsync(warm, ct).ConfigureAwait(false)) {
                response.EnsureSuccessStatusCode();
            }
            _ready = true;
            _status.OnNext($"Ready: {model} on Ollama.");
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            _status.OnNext($"Ollama failed: {ex.Message}");
        } finally {
            _prepare.Release();
        }
    }

    public async Task<IReadOnlyList<VoiceCommand>?> InterpretAsync(string utterance, VoiceContext context, CancellationToken ct) {
        if (!_ready) {
            _ = PrepareAsync(CancellationToken.None);
            return null;
        }
        using var request = Post("api/chat", json => {
            json.WriteString("model", model);
            json.WriteBoolean("stream", false);
            json.WriteString("keep_alive", KeepAlive);
            // A thinking model would reason before the JSON, which costs seconds for a one-word choice.
            json.WriteBoolean("think", false);
            json.WritePropertyName("format");
            json.WriteRawValue(VoiceIntent.Schema);
            json.WriteStartObject("options");
            json.WriteNumber("temperature", 0);
            json.WriteNumber("num_ctx", ContextTokens);
            json.WriteEndObject();
            json.WriteStartArray("messages");
            WriteMessage(json, "system", VoiceIntent.SystemPrompt);
            WriteMessage(json, "user", VoiceIntent.Message(utterance, context));
            json.WriteEndArray();
        });
        try {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var content = doc.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c)
                ? c.GetString() : null;
            return content is null ? null : VoiceIntent.Read(content, utterance, context);
        } catch (Exception ex) when (ex is HttpRequestException or JsonException) {
            _ready = false;
            _status.OnNext($"Ollama failed: {ex.Message}");
            return null;
        }
    }

    async Task<bool> RespondsAsync(CancellationToken ct) {
        try {
            using var response = await http.GetAsync(new Uri(baseUrl, "api/version"), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        } catch (HttpRequestException) {
            return false;
        }
    }

    async Task<bool> HasModelAsync(CancellationToken ct) {
        using var response = await http.GetAsync(new Uri(baseUrl, "api/tags"), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return false;
        // Ollama lists "qwen2.5:3b"; a bare name means ":latest".
        var wanted = model.Contains(':', StringComparison.Ordinal) ? model : model + ":latest";
        return models.EnumerateArray().Any(m => m.TryGetProperty("name", out var name) && name.GetString() == wanted);
    }

    async Task PullAsync(CancellationToken ct) {
        _status.OnNext($"Downloading {model}…");
        using var request = Post("api/pull", json => {
            json.WriteString("model", model);
            json.WriteBoolean("stream", true);
        });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error)) throw new HttpRequestException(error.GetString());
            if (root.TryGetProperty("total", out var total) && root.TryGetProperty("completed", out var completed)
                && total.GetInt64() > 0)
                _status.OnNext(string.Create(CultureInfo.InvariantCulture,
                    $"Downloading {model}… {completed.GetInt64() * 100 / total.GetInt64()}%"));
        }
    }

    HttpRequestMessage Post(string path, Action<Utf8JsonWriter> body) {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream)) {
            json.WriteStartObject();
            body(json);
            json.WriteEndObject();
        }
        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = Encoding.UTF8.WebName };
        return new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, path)) { Content = content };
    }

    static void WriteMessage(Utf8JsonWriter json, string role, string content) {
        json.WriteStartObject();
        json.WriteString("role", role);
        json.WriteString("content", content);
        json.WriteEndObject();
    }

    public void Dispose() {
        _status.Dispose();
        _prepare.Dispose();
    }
}
