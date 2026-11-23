using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>Codex's <c>models_cache.json</c>. Every Codex build sharing a home refetches and rewrites
/// it on finding another build's <c>client_version</c>, and builds disagree on the model entries, so
/// alternating builds rewrite it many times an hour with no change to what this daemon's Codex lists.</summary>
internal static class CodexModelsCache {
    /// <summary>Whether <paramref name="content"/> was written by Codex <paramref name="version"/>. An
    /// unknown version, or a cache without a readable stamp, counts as written by it.</summary>
    internal static bool WrittenBy(byte[] content, string? version) {
        if (string.IsNullOrEmpty(version)) return true;

        try {
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.Str("client_version") is not { } stamp || string.Equals(stamp, version, StringComparison.Ordinal);
        } catch (JsonException) {
            return true;
        }
    }
}
