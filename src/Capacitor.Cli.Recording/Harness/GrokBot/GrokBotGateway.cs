using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>
/// Client for the Grok Bot cloud computer's local gateway, an undocumented internal API: every call is
/// <c>POST /api/{method}</c> with the bearer token from <c>gateway.json</c>. The descriptor is re-read on
/// every call, because the gateway restarts with a new port and token.
/// </summary>
public sealed class GrokBotGateway(string descriptorPath, HttpClient http) : IGrokBotSource {
    public const int TailLimit = 200;

    /// <summary>A chat longer than this many tail pages is not read back to its first entry.</summary>
    public const int MaxPages = 50;

    public string Description => $"the gateway at {descriptorPath}";

    // Fields that move when a chat gains or changes an entry. lastEntry and localActivityAt look as if they
    // should and do not: one previews how the last message opens, the other is the chat's creation time.
    static readonly string[] ChangeFields =
        ["newestEntryId", "snapshotSeq", "updatedAt", "lastActivityAt", "lastMessageId", "isRunningTurn", "awaitingUserResponse"];

    public async Task<IReadOnlyList<GrokBotAgent>> ListAgentsAsync(CancellationToken ct) {
        using var doc = await CallAsync("listAgents", new JsonObject(), ct);

        // An answer with no list is no listing at all: read as no chats, it would pass for a healthy cycle
        // while nothing records.
        if ((doc.RootElement.IsArray ? doc.RootElement : doc.RootElement.Arr("agents")) is not { } list)
            throw new InvalidDataException("gateway listAgents answered with no agents list");

        var result = new List<GrokBotAgent>();
        foreach (var a in list.EnumerateArray()) {
            if (a.Str("id") is not { Length: > 0 } agentId) continue;

            var fingerprint = string.Join("|", ChangeFields.Select(f => a.Prop(f)?.GetRawText() ?? ""));

            result.Add(new GrokBotAgent(agentId, a.Str("name") ?? "", a.Bool("isRunningTurn") == true, fingerprint));
        }

        return result;
    }

    /// <summary>The newest page, then earlier pages until one reaches <paramref name="lastSeq"/> or the
    /// chat's first entry. Unreadable when a resumed read cannot get back to <paramref name="lastSeq"/>.</summary>
    public async Task<GrokBotRead> ReadSinceAsync(string agentId, int lastSeq, CancellationToken ct) {
        if (await TailAsync(agentId, null, ct) is not var (entries, before)) return GrokBotRead.Unreadable;

        var all   = new List<GrokBotEntry>(entries);
        var floor = Math.Max(lastSeq + 1, 1);

        // Paged by the cursor, not by what a page parsed to: a page of entries this reader rejects still
        // has earlier pages behind it.
        for (var pages = 1; before is { } cursor && cursor > floor; pages++) {
            // The budget bounds a first read only. A resumed read stops short of nothing: whatever it left
            // unread behind the newest pages would be passed over for good once those are delivered.
            if (lastSeq < 0 && pages >= MaxPages) return new(all, false);

            if (await TailAsync(agentId, cursor, ct) is not var (older, next)) return GrokBotRead.Unreadable;

            if (next >= cursor) return lastSeq < 0 ? new(all.Concat(older).ToList(), false) : GrokBotRead.Unreadable;

            all.AddRange(older);
            before = next;
        }

        return new(all, true);
    }

    /// <summary>The newest entries, or the page before <paramref name="beforeSeq"/>. Returns the
    /// parsed entries and the cursor for the page before them, when the gateway gives one; null for an
    /// answer with no entries list, which is no page at all: read as an empty one with no cursor, it
    /// would pass for the chat's beginning.</summary>
    public async Task<(IReadOnlyList<GrokBotEntry> Entries, int? NextBeforeSeq)?> TailAsync(
            string agentId, int? beforeSeq, CancellationToken ct) {
        var body = new JsonObject { ["id"] = agentId, ["limit"] = TailLimit };
        if (beforeSeq is { } before) body["beforeSeq"] = before;

        using var doc = await CallAsync("getAgentTranscriptTail", body, ct);
        var root = doc.RootElement;

        if (root.Arr("entries") is not { } list) return null;

        var entries = new List<GrokBotEntry>();
        foreach (var e in list.EnumerateArray())
            if (GrokBotEntry.TryParse(e, agentId) is { } entry) entries.Add(entry);

        int? next = root.Num("nextBeforeSeq") is { } n and >= int.MinValue and <= int.MaxValue ? (int)n : null;

        return (entries, next);
    }

    async Task<JsonDocument> CallAsync(string method, JsonObject body, CancellationToken ct) {
        var (baseUrl, token) = ReadDescriptor();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"gateway {method} answered {(int)response.StatusCode}", null, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    (string BaseUrl, string Token) ReadDescriptor() {
        // The gateway rewrites its descriptor on every restart, so the read must not deny that write.
        var root   = JsonNode.Parse(File.ReadAllTextShared(descriptorPath))!.AsObject();
        var scheme = root["scheme"]?.GetValue<string>() is { Length: > 0 } s ? s : "http";
        var host   = root["host"]?.GetValue<string>() is { Length: > 0 } h && h != "0.0.0.0" ? h : "127.0.0.1";
        var port   = root["port"]?.GetValue<int>() ?? throw new InvalidDataException("gateway.json has no port");
        var token  = root["token"]?.GetValue<string>() ?? throw new InvalidDataException("gateway.json has no token");

        return ($"{scheme}://{host}:{port}", token);
    }
}
