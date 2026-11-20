using Capacitor.Client;

namespace Capacitor.Cli.Ide;

internal static class ConnectionMapping {
    public static ConnectionChanged From(AttachStatus daemon, string daemonName, ServerLaneStatus lane) => new(
        new DaemonConnection(DaemonState(daemon.State), daemonName, daemon.Reason),
        new ServerConnection(ServerState(lane.State), lane.Detail));

    // A state added later goes out as its own name: these run inside Rx callbacks, where a throw would end the feed.
    static string DaemonState(AttachState state) => state switch {
        AttachState.Connecting  => "connecting",
        AttachState.Connected   => "attached",
        AttachState.Unreachable => "unreachable",
        _                       => state.ToString().ToLowerInvariant(),
    };

    static string ServerState(ServerLaneState state) => state switch {
        ServerLaneState.Dormant    => "dormant",
        ServerLaneState.Connecting => "connecting",
        ServerLaneState.Connected  => "connected",
        ServerLaneState.Retrying   => "retrying",
        ServerLaneState.SignedOut  => "signed_out",
        _                          => state.ToString().ToLowerInvariant(),
    };
}
