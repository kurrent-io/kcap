using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Harness.Grok;

/// <summary>
/// Reads a finished Grok turn's token usage, one envelope per model, from the <c>turn_completed</c>
/// frame Grok sends outside <c>session/update</c>. <c>inputTokens</c> is gross, cached reads included.
/// </summary>
internal static class GrokTurnUsage {
    // session/load replays a finished turn's frame under _x.ai/session/update instead.
    const string LiveMethod = "_x.ai/session_notification";

    internal static IReadOnlyList<AcpEventEnvelope> Read(AcpNotification notification) {
        if (notification.Method != LiveMethod
         || notification.Params?.Obj("update") is not { } update
         || update.Str("sessionUpdate") != "turn_completed"
         || update.Obj("usage")?.Obj("modelUsage") is not { } byModel)
            return [];

        List<AcpEventEnvelope>? usage = null;
        foreach (var model in byModel.EnumerateObject()) {
            if (!model.Value.IsObject || model.Name.Length == 0) continue;

            var input      = model.Value.Num("inputTokens");
            var output     = model.Value.Num("outputTokens");
            var cacheWrite = model.Value.Num("cacheCreationTokens");

            // Cached reads are inside inputTokens. A total that also counts cache creation on top of
            // input and output says creation is outside it, and the envelope carries input gross of both.
            if (input is { } i && cacheWrite is > 0 && model.Value.Num("totalTokens") is { } total
             && total >= i + (output ?? 0) + cacheWrite.Value)
                input = i + cacheWrite.Value;

            (usage ??= []).Add(new AcpEventEnvelope(
                Kind:                       AcpEventKind.TokenUsage,
                Model:                      model.Name,
                UsageInputTokens:           input,
                UsageCachedInputTokens:     model.Value.Num("cachedReadTokens"),
                UsageCacheWriteInputTokens: cacheWrite,
                UsageOutputTokens:          output,
                UsageReasoningTokens:       model.Value.Num("reasoningTokens")));
        }

        return usage ?? [];
    }
}
