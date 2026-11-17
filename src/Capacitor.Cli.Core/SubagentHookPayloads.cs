using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>
/// Bodies for <c>/hooks/subagent-start</c> and <c>/hooks/subagent-stop</c> on behalf of a vendor that fires no
/// subagent hook of its own. The server binds every field of the stop shape, so a body missing one is rejected
/// before <c>strict</c> is honoured; <c>cwd</c> is empty because the child shares its parent's.
/// </summary>
public static class SubagentHookPayloads {
    /// <param name="spawnCallId">The parent's call that spawned the agent, so the server seats the start on its turn.</param>
    /// <param name="strict">Ask the server to refuse, rather than accept, a lifecycle write it failed to record.</param>
    public static JsonObject Start(
            string parentSessionId, string agentId, string agentType, string transcriptPath, string? spawnCallId = null, bool strict = false
        ) {
        var payload = new JsonObject {
            ["hook_event_name"] = "subagent_start",
            ["session_id"]      = parentSessionId,
            ["agent_id"]        = agentId,
            ["agent_type"]      = agentType,
            ["transcript_path"] = transcriptPath,
            ["cwd"]             = "",
        };

        if (spawnCallId is not null) payload["spawn_call_id"] = spawnCallId;
        if (strict) payload["strict"]                         = true;

        return payload;
    }

    /// <param name="strict">Ask the server to refuse, rather than accept, a lifecycle write it failed to record.</param>
    public static JsonObject Stop(string parentSessionId, string agentId, string agentType, string transcriptPath, bool strict = false) {
        var payload = new JsonObject {
            ["hook_event_name"]        = "subagent_stop",
            ["session_id"]             = parentSessionId,
            ["agent_id"]               = agentId,
            ["agent_type"]             = agentType,
            ["transcript_path"]        = transcriptPath,
            ["cwd"]                    = "",
            ["stop_hook_active"]       = false,
            ["agent_transcript_path"]  = transcriptPath,
            ["last_assistant_message"] = "",
        };

        if (strict) payload["strict"] = true;

        return payload;
    }
}
