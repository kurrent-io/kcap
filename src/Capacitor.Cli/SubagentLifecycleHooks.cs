using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli;

/// <summary>Lifecycle posts a teardown makes for a subagent whose vendor fires no subagent hook of its own.</summary>
static class SubagentLifecycleHooks {
    /// <summary>Posts <c>/hooks/subagent-stop</c> through the retrying client and returns the final status,
    /// leaving its classification to the caller. Throws on a transport failure the retries did not absorb.</summary>
    internal static async Task<int> PostStopAsync(
            ProfileContext profiles, ICapacitorHttpClient http, TimeProvider time,
            string sessionId, string agentId, string agentType, string transcriptPath
        ) {
        using var client  = await http.ForBackgroundAsync();
        var       payload = SubagentHookPayloads.Stop(sessionId, agentId, agentType, transcriptPath);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp    = await client.PostWithRetryAsync($"{profiles.Resolution.ServerUrl}/hooks/subagent-stop", content, time);
        return (int)resp.StatusCode;
    }
}
