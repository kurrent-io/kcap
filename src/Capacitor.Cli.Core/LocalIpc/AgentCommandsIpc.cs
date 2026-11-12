namespace Capacitor.Cli.Core.LocalIpc;

public static class AgentCommandsIpc {
    /// <summary>Advertised by a daemon that answers <see cref="FrameType.AgentCommands"/>.</summary>
    public const string Capability = "agent-commands/1";
}
