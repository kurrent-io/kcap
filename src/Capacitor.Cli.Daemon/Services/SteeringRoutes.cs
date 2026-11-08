using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Daemon.Harness.Claude;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The steering socket's routes, which the kcap-steering mod is the only client of. A body that is not what a route
/// takes is answered 400 <c>invalid_request</c>; every other answer is 200, except <c>/next</c> with nothing to hand
/// over, which is 204.
/// </summary>
internal sealed class SteeringRoutes(ModAttachments attachments) {
    static readonly byte[] Done = "{}"u8.ToArray();

    static readonly SteeringHttpResponse Invalid = SteeringHttpResponse.Error(400, "invalid_request");

    /// <summary>A process key is <c>pid:start time</c> as the mod builds it: printable, and short.</summary>
    const int MaxProcessKey = 256;

    const int MaxReason = 100;

    public IReadOnlyDictionary<string, Func<SteeringHttpRequest, CancellationToken, Task<SteeringHttpResponse>>> Table() =>
        new Dictionary<string, Func<SteeringHttpRequest, CancellationToken, Task<SteeringHttpResponse>>>(StringComparer.Ordinal) {
            ["/attach"]      = AttachAsync,
            ["/next"]        = NextAsync,
            ["/link"]        = LinkAsync,
            ["/undelivered"] = UndeliveredAsync,
            ["/detach"]      = DetachAsync,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    async Task<SteeringHttpResponse> AttachAsync(SteeringHttpRequest request, CancellationToken ct) {
        if (Read(request, SteeringJsonContext.Default.SteeringAttachRequest) is not { SessionId: { } session, ProcessKey: { } process } body
         || !Guid.TryParse(session, out _) || !IsProcessKey(process)
         || body.ReplacesAgentId is { } replaces && !ModAttachments.IsAgentId(replaces))
            return Invalid;

        return Json(await attachments.AttachAsync(session, process, body.ReplacesAgentId, ct), SteeringJsonContext.Default.SteeringAttachReply);
    }

    async Task<SteeringHttpResponse> NextAsync(SteeringHttpRequest request, CancellationToken ct) {
        if (Read(request, SteeringJsonContext.Default.SteeringNextRequest) is not { AgentId: { } agentId } body
         || !ModAttachments.IsAgentId(agentId)
         || body.ProcessKey is { } process && !IsProcessKey(process))
            return Invalid;

        Guid? outstanding = null;

        if (body.Outstanding is { } held) {
            if (!Guid.TryParse(held, out var id)) return Invalid;

            outstanding = id;
        }

        return await attachments.NextAsync(agentId, body.ProcessKey, body.Idle, outstanding) is { } reply
            ? Json(reply, SteeringJsonContext.Default.SteeringNextReply)
            : new SteeringHttpResponse(204, []);
    }

    Task<SteeringHttpResponse> LinkAsync(SteeringHttpRequest request, CancellationToken ct) {
        if (Read(request, SteeringJsonContext.Default.SteeringLinkRequest) is not { AgentId: { } agentId, DispatchId: { } dispatch, SessionId: { } session, RowUuid: { } row }
         || !ModAttachments.IsAgentId(agentId) || !Guid.TryParse(session, out _)
         || !Guid.TryParse(dispatch, out var dispatchId) || dispatchId == Guid.Empty
         || !Guid.TryParse(row, out var rowUuid) || rowUuid == Guid.Empty)
            return Task.FromResult(Invalid);

        attachments.Link(agentId, dispatchId, session, rowUuid);

        return Task.FromResult(new SteeringHttpResponse(200, Done));
    }

    Task<SteeringHttpResponse> UndeliveredAsync(SteeringHttpRequest request, CancellationToken ct) {
        if (Read(request, SteeringJsonContext.Default.SteeringUndeliveredRequest) is not { AgentId: { } agentId, DispatchId: { } dispatch, Outcome: "rejected" or "unconfirmed", Reason: { } reason } body
         || !ModAttachments.IsAgentId(agentId) || !IsReason(reason)
         || !Guid.TryParse(dispatch, out var dispatchId) || dispatchId == Guid.Empty)
            return Task.FromResult(Invalid);

        attachments.Undelivered(agentId, dispatchId, rejected: body.Outcome == "rejected", reason);

        return Task.FromResult(new SteeringHttpResponse(200, Done));
    }

    async Task<SteeringHttpResponse> DetachAsync(SteeringHttpRequest request, CancellationToken ct) {
        if (Read(request, SteeringJsonContext.Default.SteeringDetachRequest) is not { AgentId: { } agentId } body
         || !ModAttachments.IsAgentId(agentId)
         || body.ProcessKey is { } process && !IsProcessKey(process))
            return Invalid;

        await attachments.DetachAsync(agentId, body.ProcessKey);

        return new SteeringHttpResponse(200, Done);
    }

    static T? Read<T>(SteeringHttpRequest request, JsonTypeInfo<T> type) where T : class {
        try {
            return JsonSerializer.Deserialize(request.Body, type);
        } catch (JsonException) {
            return null;
        }
    }

    static SteeringHttpResponse Json<T>(T reply, JsonTypeInfo<T> type) => new(200, JsonSerializer.SerializeToUtf8Bytes(reply, type));

    static bool IsProcessKey(string key) => key.Length is > 0 and <= MaxProcessKey && key.All(c => c is >= ' ' and <= '~');

    static bool IsReason(string reason) =>
        reason.Length is > 0 and <= MaxReason && reason.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
