using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Hash of the part of a catalog file that can change what a vendor may launch. JSON is
/// hashed without its volatile members — cache freshness stamps and rotating credentials, which
/// vendors rewrite on every refetch or token refresh — so an unchanged model list or account reads
/// as unchanged. Anything else is hashed byte for byte.</summary>
internal static class CatalogContentDigest {
    static readonly FrozenSet<string> Volatile = FrozenSet.Create(StringComparer.Ordinal,
        // Codex: models_cache.json, auth.json
        "fetched_at", "etag", "id_token", "access_token", "refresh_token", "last_refresh",
        // Pi: auth.json, per provider
        "access", "refresh", "expires");

    internal static string Of(byte[] content) {
        if (Canonical(content) is { } canonical) content = canonical;
        return Convert.ToHexString(SHA256.HashData(content));
    }

    static byte[]? Canonical(byte[] content) {
        try {
            using var doc    = JsonDocument.Parse(content);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) Write(writer, doc.RootElement);
            return buffer.ToArray();
        } catch (JsonException) {
            return null;
        }
    }

    static void Write(Utf8JsonWriter writer, JsonElement element) {
        switch (element.ValueKind) {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()) {
                    if (Volatile.Contains(property.Name)) continue;
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
