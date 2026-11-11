using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Capture CommandAck and CommandRejected synchronously so awaited submission exposes both sends.
/// Force IsReady and override AgentRegisteredAsync: successful launches must not call the base
/// HubConnection, which this double never starts. No-op other capacity-rejection notifications.
/// </summary>
internal sealed class SeqCaptureServerConnection() : ServerConnection(
    new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
    new FixedCredentialSource(),
    NullLoggerFactory.Instance,
    NullLogger<ServerConnection>.Instance
, TimeProvider.System) {
    internal override bool                                  IsReady       => true;
    public            List<CommandRejected>                 Rejects       { get; } = [];
    public            List<CommandAck>                      Acks          { get; } = [];
    public            List<(string AgentId, string Reason)> LaunchFaileds { get; } = [];

    public override Task LaunchFailedAsync(string             agentId, string reason) { lock (LaunchFaileds) LaunchFaileds.Add((agentId, reason)); return Task.CompletedTask; }
    public override Task CommandRejectedAsync(CommandRejected rej) { lock (Rejects) Rejects.Add(rej); return Task.CompletedTask; }
    public override Task CommandAckAsync(CommandAck           ack)           { lock (Acks)    Acks.Add(ack);    return Task.CompletedTask; }

    public override Task<AgentRegisteredResponse> AgentRegisteredAsync(
        string  agentId,              string? prompt, string? model, string? effort, string? repoPath,
        string? sandboxPolicy = null, string? approvalPolicy = null, string? permissionPreset = null,
        string? runtimeTransport = null, string? vendor = null, AgentStartTitle? title = null, string? continuesAgentId = null,
        string? accountDirectory = null, string? accountId = null, string? accountLabel = null,
        string? resumedSessionId = null, bool resumeFirstAnnounce = false) => Task.FromResult(AgentRegisteredResponse.Accepted);
}
