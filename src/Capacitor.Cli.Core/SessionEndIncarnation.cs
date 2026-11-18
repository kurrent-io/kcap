using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>Names on a session end the hosted agent and daemon epoch whose process it reports, so the
/// server can refuse an end a resumed agent's predecessor sends after the resume.</summary>
public static class SessionEndIncarnation {
    public static void Stamp(JsonNode end, string? agentId, string? daemonEpoch) {
        if (agentId is not null) end["agent_host_id"] = agentId;
        if (daemonEpoch is not null) end["daemon_epoch"] = daemonEpoch;
    }

    public static void Stamp(JsonNode end, HostedAgent hosted) => Stamp(end, hosted.AgentId, hosted.DaemonEpoch);
}
