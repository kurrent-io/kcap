using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The title, kind and input each tool call announced in its <c>tool_call</c> updates, for a vendor whose
/// <c>session/request_permission</c> names the call by <c>toolCallId</c> alone. Without them the approval card,
/// the launch's policy and its permission preset would all see a call with nothing to judge.
/// </summary>
internal sealed class AcpAnnouncedToolCalls {
    static readonly string[] Fields = ["title", "kind", "rawInput"];

    /// <summary>A permission request follows its call's announcement within the turn, so only recent calls matter.</summary>
    const int Capacity = 256;

    readonly Lock _lock = new();
    readonly Dictionary<string, Dictionary<string, JsonElement>> _calls = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();

    /// <summary>Records the fields a <c>tool_call</c> or <c>tool_call_update</c> carries; a later value wins.</summary>
    public void Observe(JsonElement update) {
        if (update.Str("sessionUpdate") is not ("tool_call" or "tool_call_update")) return;
        if (update.Str("toolCallId") is not { Length: > 0 } id) return;

        lock (_lock) {
            if (!_calls.TryGetValue(id, out var fields)) {
                fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                _calls[id] = fields;
                _order.Enqueue(id);

                if (_order.Count > Capacity) _calls.Remove(_order.Dequeue());
            }

            foreach (var name in Fields)
                if (update.Prop(name) is { ValueKind: not JsonValueKind.Null } value)
                    fields[name] = value.Clone();
        }
    }

    /// <summary>The request with each field its <c>toolCall</c> lacks filled in from the call's announcement. A
    /// field the request carries is never replaced, and any other request is returned as it came.</summary>
    public AcpRequest Complete(AcpRequest request) {
        if (request.Method != "session/request_permission"
         || request.Params is not { } parameters
         || parameters.Obj("toolCall") is not { } toolCall
         || toolCall.Str("toolCallId") is not { Length: > 0 } id)
            return request;

        Dictionary<string, JsonElement> missing;

        lock (_lock) {
            if (!_calls.TryGetValue(id, out var fields)) return request;
            missing = fields.Where(f => toolCall.Prop(f.Key) is null).ToDictionary(StringComparer.Ordinal);
        }

        if (missing.Count == 0) return request;

        var completed = JsonNode.Parse(parameters.GetRawText())!.AsObject();
        var call      = completed["toolCall"]!.AsObject();

        foreach (var (name, value) in missing) call[name] = JsonNode.Parse(value.GetRawText());

        using var document = JsonDocument.Parse(completed.ToJsonString());

        return request with { Params = document.RootElement.Clone() };
    }
}
