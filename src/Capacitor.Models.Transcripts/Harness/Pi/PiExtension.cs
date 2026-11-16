using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Pi;

/// The <c>pi</c> extension slug. Every block writes each of its fields, an absent one as null, because the
/// server's stored Pi events carry the whole shape.
public static class PiExtension {
    public const string Slug = "pi";

    public static Struct Session(string? cwd, int? version, string? parentSession) => Block(
        ("cwd", Text(cwd)),
        ("version", Number(version)),
        ("parent_session", Text(parentSession)),
        ("source", Value.ForNull()),
        ("pi_version", Value.ForNull()));

    /// On an assistant message's first event. <paramref name="cost"/> is Pi's own dollar figure for the
    /// message, which has no canonical home.
    public static Struct Turn(string? provider, string? model, string? stopReason, string? parentId, double? cost) => Block(
        ("provider", Text(provider)),
        ("model", Text(model)),
        ("stop_reason", Text(stopReason)),
        ("parent_id", Text(parentId)),
        ("cost", Number(cost)));

    public static Struct BranchSummary(string? fromId, string? parentId, string summary, JsonElement? details, bool? fromHook) => Block(
        ("from_id", Text(fromId)),
        ("parent_id", Text(parentId)),
        ("summary", Text(summary)),
        ("details", Json(details)),
        ("from_hook", Flag(fromHook)));

    /// <paramref name="success"/> is the inverse of Pi's <c>isError</c>, null when the flag was absent.
    public static Struct ToolResult(
            bool?        success,
            string?      toolName,
            string?      details     = null,
            JsonElement? detailsJson = null,
            Struct?      subagent    = null,
            string?      command     = null,
            long?        exitCode    = null,
            bool?        userBash    = null
        ) => Block(
        ("success", Flag(success)),
        ("tool_name", Text(toolName)),
        ("details", Text(details)),
        ("details_json", Json(detailsJson)),
        ("subagent", subagent is null ? Value.ForNull() : Value.ForStruct(subagent)),
        ("command", Text(command)),
        ("exit_code", Number(exitCode)),
        ("user_bash", Flag(userBash)));

    internal static Struct Subagent(
            string? mode,
            string? agentScope,
            string? projectAgentsDir,
            int?    resultCount,
            int?    successCount,
            int?    failureCount,
            long?   inputTokens,
            long?   outputTokens,
            long?   cacheReadTokens,
            long?   cacheWriteTokens,
            long?   contextTokens,
            long?   turns,
            double? cost
        ) => Block(
        ("mode", Text(mode)),
        ("agent_scope", Text(agentScope)),
        ("project_agents_dir", Text(projectAgentsDir)),
        ("result_count", Number(resultCount)),
        ("success_count", Number(successCount)),
        ("failure_count", Number(failureCount)),
        ("input_tokens", Number(inputTokens)),
        ("output_tokens", Number(outputTokens)),
        ("cache_read_tokens", Number(cacheReadTokens)),
        ("cache_write_tokens", Number(cacheWriteTokens)),
        ("context_tokens", Number(contextTokens)),
        ("turns", Number(turns)),
        ("cost", Number(cost)));

    static Struct Block(params ReadOnlySpan<(string Field, Value Value)> fields) {
        var block = new Struct();
        foreach (var (field, value) in fields) block.Fields[field] = value;
        return block;
    }

    static Value Text(string? text) => text is null ? Value.ForNull() : Value.ForString(text);

    static Value Number(double? number) => number is { } n ? Value.ForNumber(n) : Value.ForNull();

    static Value Flag(bool? flag) => flag is { } f ? Value.ForBool(f) : Value.ForNull();

    static Value Json(JsonElement? element) => element is { } e ? Value.Parser.ParseJson(e.GetRawText()) : Value.ForNull();
}
