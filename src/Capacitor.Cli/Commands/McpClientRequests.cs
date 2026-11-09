using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>Requests a server sends its client, such as an elicitation, each matched to the client's answer by id.
/// <see cref="McpStdioLoop"/> attaches the writer and routes the answers; once input ends, every request still waiting
/// gets no answer rather than waiting forever.</summary>
sealed class McpClientRequests {
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject?>> _pending = new(StringComparer.Ordinal);
    Func<string, Task>? _write;
    long _next;
    volatile bool _closed;

    internal void Attach(Func<string, Task> write) => _write = write;

    /// <summary>The client's <c>result</c>, or null when it answered with an error, input ended, or nothing is
    /// attached.</summary>
    public async Task<JsonObject?> SendAsync(string method, JsonObject parameters, CancellationToken ct) {
        if (_write is not { } write) return null;

        var id     = $"kcap-{Interlocked.Increment(ref _next)}";
        var answer = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;

        try {
            if (_closed) return null;

            await write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString());

            await using var cancel = ct.Register(() => answer.TrySetCanceled(ct));

            return await answer.Task;
        } finally {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>True when <paramref name="response"/> answers a request sent here.</summary>
    internal bool TryComplete(JsonObject response) {
        if (response["id"] is not JsonValue value || !value.TryGetValue(out string? id) || !_pending.TryGetValue(id, out var answer)) return false;

        answer.TrySetResult(response["result"] as JsonObject);

        return true;
    }

    internal void Close() {
        _closed = true;

        foreach (var answer in _pending.Values) answer.TrySetResult(null);
    }
}
