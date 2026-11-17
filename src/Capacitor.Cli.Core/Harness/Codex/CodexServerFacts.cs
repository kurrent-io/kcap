using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Codex;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Cli.Core.Harness.Codex;

/// A user message's turn id and content item kinds, which the server keeps outside the message: its
/// session-detail read carries them as `vendors.codex`, in the shape of the message's own `codex` slug,
/// and its live tail as the event metadata keys below.
static class CodexServerFacts {
    const string TurnIdKey = "$codexTurnId";

    /// Comma-joined; Codex's kind names hold no commas.
    const string ContentItemKindsKey = "$codexContentItemKinds";

    /// Puts what the server kept onto the message's `codex` slug; a field the message already carries wins.
    public static void Restore(UserMessageReceived message, JsonElement? vendors, string? metadata) {
        if ((FromVendors(vendors) ?? FromMetadata(metadata)) is not { } facts) return;
        var block = CodexExtension.Pack(facts, CodexJsonContext.Default.CodexUserMessageExtension);
        if (!message.Extensions.TryGetValue(CodexExtension.Slug, out var slug)) {
            message.Extensions[CodexExtension.Slug] = block;
            return;
        }
        foreach (var (field, value) in block.Fields)
            if (!slug.Fields.ContainsKey(field)) slug.Fields[field] = value;
    }

    static CodexUserMessageExtension? FromVendors(JsonElement? vendors) =>
        vendors?.Obj(CodexExtension.Slug) is { } codex
            ? Facts(codex.Str("turn_id"), codex.Arr(CodexExtension.ContentItemKinds)?.EnumerateArray().Where(k => k.IsString).Select(k => k.GetString()!).ToList())
            : null;

    static CodexUserMessageExtension? FromMetadata(string? metadata) {
        if (string.IsNullOrEmpty(metadata)) return null;
        try {
            using var doc = JsonDocument.Parse(metadata);
            var root = doc.RootElement;
            return Facts(root.Str(TurnIdKey), root.Str(ContentItemKindsKey)?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());
        } catch (JsonException) {
            return null;
        }
    }

    static CodexUserMessageExtension? Facts(string? turnId, List<string>? kinds) {
        if (turnId is { Length: 0 }) turnId = null;
        return turnId is null && kinds is null ? null : new CodexUserMessageExtension { TurnId = turnId, ContentItemKinds = kinds };
    }
}
