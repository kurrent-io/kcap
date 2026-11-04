using System.Text.Encodings.Web;
using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>
/// Neither Pi nor OpenCode constrains its output to a schema, so the schema travels in the prompt and
/// the reply is checked here.
/// </summary>
internal static class StructuredReply {
    static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string SchemaInstruction(string schema) =>
        "Reply with exactly one JSON value that matches the JSON Schema below. " +
        "Output only that JSON value, with no commentary and no Markdown code fence.\n\n" +
        "JSON Schema:\n" + schema;

    /// The reply re-serialised when it is one JSON value that matches the schema, else null.
    public static string? Accept(string reply, string schema) {
        try {
            using var schemaDoc = JsonDocument.Parse(schema);
            using var replyDoc = JsonDocument.Parse(StripFence(reply));
            if (!JsonSchemaCheck.Matches(schemaDoc.RootElement, replyDoc.RootElement)) return null;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
                replyDoc.RootElement.WriteTo(writer);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        } catch (JsonException) {
            return null;
        }
    }

    static string StripFence(string reply) {
        var text = reply.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstLine = text.IndexOf('\n');
        if (firstLine < 0) return text;

        var body = text[(firstLine + 1)..].TrimEnd();
        return body.EndsWith("```", StringComparison.Ordinal) ? body[..^3].TrimEnd() : body;
    }
}
