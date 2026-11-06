using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Recording;

/// <summary>What a vendor's subagent scan draws on from the recorder running it.</summary>
internal sealed record SubagentScanContext(
        IRecorderHost Host, ConfigRoot Config, ProfileContext Profiles, ICapacitorHttpClient Http, TimeProvider Time, Action<string> Log) {
    public string Url => Profiles.Resolution.ServerUrl!;

    public async Task<bool> PostSubagentStartAsync(
        string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var client  = await Http.ForBackgroundAsync(ct);
            var       payload = GeminiSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/subagent-start", content, Time, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }
}
