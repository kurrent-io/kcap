using System.Text.Json;

namespace Capacitor.Cli.Harness.Dsh;

/// <summary>The <c>{"$kcap":"header", ...}</c> line the kcap plugin writes first: dsh's own session
/// header (cwd, creation time, the parent of a subagent) under kcap's marker.</summary>
internal sealed record DshSessionHeader(string? Cwd, DateTimeOffset? CreatedAt, string? ParentSession) {
    public static DshSessionHeader? TryRead(string transcriptPath) {
        try {
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            for (var i = 0; i < 8 && reader.ReadLine() is { } line; i++) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.Str("$kcap") != "header") continue;

                DateTimeOffset? createdAt = root.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ca.GetInt64())
                    : null;

                return new DshSessionHeader(root.Str("cwd"), createdAt, root.Str("parentSession"));
            }
        } catch {
            // A header that cannot be read must not stop discovery.
        }
        return null;
    }
}
