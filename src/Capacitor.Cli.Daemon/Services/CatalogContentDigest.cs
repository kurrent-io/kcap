using System.Buffers.Text;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Hash of the part of a catalog file that can change what a vendor may launch. JSON is
/// hashed without its volatile members — cache freshness stamps and rotating credentials, which
/// vendors rewrite on every refetch or token refresh — so an unchanged model list or account reads
/// as unchanged. An ID token is hashed by its claims, since Codex's plan and entitlements ride
/// there. Anything else is hashed byte for byte.</summary>
internal static class CatalogContentDigest {
    static readonly FrozenSet<string> Volatile = FrozenSet.Create(StringComparer.Ordinal,
        // Codex: models_cache.json, auth.json
        "fetched_at", "etag", "access_token", "refresh_token", "last_refresh",
        // Pi: auth.json, per provider
        "access", "refresh", "expires",
        // ID token claims a refresh reissues
        "iat", "exp", "nbf", "auth_time", "rat", "sid", "jti", "at_hash", "c_hash", "chatgpt_subscription_last_checked");

    const string IdToken = "id_token";

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
                // Ordered by name: a vendor serializing a map may emit its keys in any order.
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) {
                    if (Volatile.Contains(property.Name)) continue;
                    writer.WritePropertyName(property.Name);
                    if (!property.NameEquals(IdToken)) Write(writer, property.Value);
                    else if (Claims(property.Value) is { } claims)
                        using (claims) Write(writer, claims.RootElement);
                    else writer.WriteNullValue();
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

    /// A JWT's payload, or null for anything that is not one.
    static JsonDocument? Claims(JsonElement token) {
        if (token.ValueKind != JsonValueKind.String || token.GetString()?.Split('.') is not [_, var payload, _]) return null;
        try {
            return JsonDocument.Parse(Base64Url.DecodeFromChars(payload));
        } catch (Exception ex) when (ex is FormatException or JsonException) {
            return null;
        }
    }
}
