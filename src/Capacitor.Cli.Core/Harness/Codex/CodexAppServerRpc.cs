using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Newline-delimited JSON-RPC to <c>codex app-server</c>, one request in flight at a time. A request from
/// the server is refused with method-not-found: this client offers nothing, and an unanswered request could stall
/// the answer it waits for.</summary>
internal sealed class CodexAppServerRpc(StreamWriter input, StreamReader output, CancellationToken ct) {
    const int MethodNotFound = -32601;

    // Vendor text lands in published errors.
    const int PreviewChars = 200;

    long _nextId;

    public sealed class CallFailed(string message) : Exception(message);

    public static string Preview(string text) {
        var trimmed = text.Trim();
        return trimmed.Length > PreviewChars ? trimmed[..PreviewChars] : trimmed;
    }

    public async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter> writeParams) {
        var id = ++_nextId;
        await SendAsync(w => {
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            w.WriteStartObject("params");
            writeParams(w);
            w.WriteEndObject();
        }).ConfigureAwait(false);

        while (true) {
            var line = await output.ReadLineAsync(ct).AsTask().WaitAsync(ct).ConfigureAwait(false)
                    ?? throw new IOException("codex app-server closed its output");

            JsonElement frame;
            try {
                using var doc = JsonDocument.Parse(line);
                frame = doc.RootElement.Clone();
            } catch (JsonException) {
                continue;
            }

            if (!frame.IsObject) continue;

            if (frame.Str("method") is { } serverMethod) {
                if (frame.Prop("id") is { } requestId) await RefuseAsync(requestId, serverMethod).ConfigureAwait(false);
                continue;
            }

            if (frame.Num("id") != id) continue;

            if (frame.Obj("error") is { } error)
                throw new CallFailed(error.Str("message") is { } message ? $"{Preview(message)} ({error.Num("code")})" : $"error {error.Num("code")}");

            return frame.Prop("result") ?? default;
        }
    }

    /// <summary>The <c>initialize</c> handshake every session opens with.</summary>
    public Task<JsonElement> InitializeAsync() =>
        RequestAsync("initialize", w => {
            w.WriteStartObject("clientInfo");
            w.WriteString("name", "kcap");
            w.WriteString("version", CapacitorVersion.Current());
            w.WriteEndObject();
        });

    Task RefuseAsync(JsonElement id, string method) =>
        SendAsync(w => {
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WriteStartObject("error");
            w.WriteNumber("code", MethodNotFound);
            w.WriteString("message", $"Method not found: {method}");
            w.WriteEndObject();
        });

    async Task SendAsync(Action<Utf8JsonWriter> body) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            body(writer);
            writer.WriteEndObject();
        }

        await input.WriteAsync((Encoding.UTF8.GetString(buffer.ToArray()) + "\n").AsMemory(), ct).ConfigureAwait(false);
        await input.FlushAsync(ct).ConfigureAwait(false);
    }
}
