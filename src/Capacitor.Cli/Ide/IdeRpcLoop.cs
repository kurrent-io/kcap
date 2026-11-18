using System.Text.Json.Nodes;

namespace Capacitor.Cli.Ide;

/// <summary>
/// Newline-delimited JSON-RPC 2.0 over a reader and a writer. Requests are answered one at a time in arrival
/// order; notifications from the peer are ignored. Every write goes through one gate, so a notification sent
/// from any thread never lands inside a response line.
/// </summary>
internal sealed class IdeRpcLoop(TextReader reader, TextWriter writer) : IDisposable {
    readonly Dictionary<string, Func<JsonObject?, CancellationToken, Task<IdeReply>>> _handlers = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _writeGate = new(1, 1);
    volatile bool _closed;

    public void Dispose() => _writeGate.Dispose();

    /// <summary>Register before <see cref="RunAsync"/>; the table is not guarded against concurrent change.</summary>
    public void Handle(string method, Func<JsonObject?, CancellationToken, Task<JsonNode?>> handler) =>
        HandleThen(method, async (parameters, ct) => new IdeReply(await handler(parameters, ct)));

    /// <inheritdoc cref="Handle"/>
    public void HandleThen(string method, Func<JsonObject?, CancellationToken, Task<IdeReply>> handler) {
        if (!_handlers.TryAdd(method, handler)) throw new InvalidOperationException($"A handler for '{method}' is already registered.");
    }

    public Task NotifyAsync(string method, JsonNode parameters) => WriteAsync(new JsonObject {
        ["jsonrpc"] = "2.0",
        ["method"]  = method,
        ["params"]  = parameters.DeepClone(),
    });

    public async Task RunAsync(CancellationToken ct) {
        while (!_closed && await reader.ReadLineAsync(ct) is { } line) {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (TryParse(line) is not { } message) {
                await WriteAsync(Error(null, IdeErrorCodes.ParseError, "parse_error", null));
                continue;
            }

            // No id is a notification from the peer; a result or an error is a response to a request this side never sends.
            if (message["id"] is not { } id || message.ContainsKey("result") || message.ContainsKey("error")) continue;

            if (message["method"] is not JsonValue methodValue || !methodValue.TryGetValue(out string? method)) {
                await WriteAsync(Error(id, IdeErrorCodes.InvalidRequest, "invalid_request", null));
                continue;
            }

            var (answer, then) = await AnswerAsync(id, method, message["params"], ct);
            await WriteAsync(answer);
            if (then is not null && !_closed) await FollowUpAsync(method, then);
        }
    }

    async Task<(JsonObject Answer, Func<Task>? Then)> AnswerAsync(JsonNode id, string method, JsonNode? parameters, CancellationToken ct) {
        if (!_handlers.TryGetValue(method, out var handler))
            return (Error(id, IdeErrorCodes.MethodNotFound, "method_not_found", null), null);
        if (parameters is not null and not JsonObject)
            return (Error(id, IdeErrorCodes.InvalidParams, "params must be an object", null), null);

        try {
            var reply  = await handler(parameters as JsonObject, ct);
            var result = reply.Result ?? new JsonObject();

            return (new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result.DeepClone() }, reply.Then);
        } catch (IdeRpcException ex) {
            return (Error(id, ex.Code, ex.Message, ex.Detail), null);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            // The peer is not told what failed; stderr keeps the detail.
            await LogAsync($"kcap ide: handler for {method} failed: {ex}");

            return (Error(id, IdeErrorCodes.InternalError, "internal_error", null), null);
        }
    }

    static async Task FollowUpAsync(string method, Func<Task> then) {
        try {
            await then();
        } catch (Exception ex) {
            await LogAsync($"kcap ide: follow-up for {method} failed: {ex}");
        }
    }

    static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data) {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data.DeepClone();

        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error };
    }

    /// <summary>One message and a single line feed, flushed, whatever the writer's <c>NewLine</c> and auto-flush. A peer that has
    /// closed its end closes the loop; later writes are dropped.</summary>
    async Task WriteAsync(JsonObject message) {
        var line = message.ToJsonString() + "\n";
        try {
            await _writeGate.WaitAsync();
        } catch (ObjectDisposedException) {
            return;
        }

        try {
            if (_closed) return;

            try {
                await writer.WriteAsync(line);
                await writer.FlushAsync(CancellationToken.None);
            } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
                _closed = true;
            }
        } finally {
            _writeGate.Release();
        }
    }

    /// <summary>Null for a line that is not a JSON object. A duplicated key surfaces on first read, so it is forced here.</summary>
    static JsonObject? TryParse(string line) {
        try {
            var message = JsonNode.Parse(line) as JsonObject;
            _ = message?.Count;

            return message;
        } catch {
            return null;
        }
    }

    static async Task LogAsync(string line) {
        try {
            await Console.Error.WriteLineAsync(line);
        } catch {
            // A broken stderr is not a protocol fault.
        }
    }
}
