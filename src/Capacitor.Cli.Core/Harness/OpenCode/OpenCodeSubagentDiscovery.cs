using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// Discovery + lifecycle-payload glue for OpenCode subagents.
/// OpenCode runs a subagent as a child session (<c>session.parentID</c>) stored in its
/// SQLite db — not on disk as JSONL — so the kcap plugin (which holds the in-process SDK
/// <c>client</c>) fetches each child's messages and writes them as native
/// <c>{info,parts}</c> JSONL into a nested dir beside the parent's transcript:
/// <c>&lt;cacheDir&gt;/&lt;parentSid&gt;/&lt;childSid&gt;.jsonl</c>. The parent watcher then scans
/// that dir (mirroring Gemini's nested-file discovery) and streams each child with its
/// canonical agentId (= childSid) → <c>AgentSubsession-{parent}-{childSid}</c>, which lines
/// up with the agentId the server surfaced from the parent's <c>task</c> tool call.
///
/// Shared by the live scan (<c>OpenCodeSubagentScan</c>) and the session-end teardown so both
/// speak one wire contract. The <c>/hooks/subagent-{start,stop}</c> bodies are the
/// vendor-agnostic shape the server's shared handlers accept.
/// </summary>
public static class OpenCodeSubagentDiscovery {
    /// <summary>Nested dir for a parent transcript's child sessions: <c>&lt;dir&gt;/&lt;parentSid&gt;/</c>.</summary>
    public static string SubagentDir(string parentTranscriptPath) {
        var dir  = Path.GetDirectoryName(parentTranscriptPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(parentTranscriptPath);
        return Path.Combine(dir, stem);
    }

    /// <summary>
    /// Child <c>*.jsonl</c> files the plugin wrote for this parent (empty when none /
    /// the dir doesn't exist). Each filename stem is the child session id.
    /// </summary>
    public static IReadOnlyList<string> EnumerateSubagentFiles(string parentTranscriptPath) {
        var subDir = SubagentDir(parentTranscriptPath);
        if (!Directory.Exists(subDir)) return [];

        try {
            return Directory.EnumerateFiles(subDir, "*.jsonl").ToList();
        } catch {
            return [];
        }
    }

    /// <summary>
    /// The subagent's agent name, read from the child transcript's <c>info.agent</c>
    /// (OpenCode stamps e.g. "general" on every message of a subagent session). Scans
    /// lines until one carries it; falls back to "subagent". Best-effort.
    /// </summary>
    public static string ResolveAgentType(string childFile) {
        try {
            foreach (var line in File.ReadLines(childFile)) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.Obj("info") is { } info && info.Str("agent") is { Length: > 0 } agent)
                    return agent;
            }
        } catch { /* best effort */ }

        return "subagent";
    }

    /// <summary>
    /// Canonical agentId from a child filename stem. OpenCode session ids (<c>ses_…</c>)
    /// carry no dashes, but canonicalize defensively to match the server's routing.
    /// </summary>
    public static string CanonicalAgentId(string childIdStem) => childIdStem.Replace("-", "");
}
