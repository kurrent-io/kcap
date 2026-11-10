using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The daemon's advertised local-control capability list, surfaced on <c>HelloReply</c>. A client gates
/// on an entry, so each must be live: either <see cref="LocalControlServer"/> routes its frames to a
/// handler, or it names fields this daemon fills on a frame it already sends (<c>experimental/1</c> and
/// <c>headless/1</c> on the status snapshot).
/// </summary>
internal static class LocalControlCapabilities {
    public static readonly IReadOnlyList<string> Current = ["consent/1", "consent/2", "consent/3", "status/1", "permission/1", "input/1", "input/2", "settings/1", "fence/1", "shutdown/1", ShutdownIpc.TurnBoundaryCapability, RestartIpc.TurnBoundaryCapability, "resume/1", AgentAttachClient.RuntimeCapability, AgentSessionIpc.Capability, "accounts/1", "experimental/1", RenameWire.Capability, HandoffWire.Capability, HeadlessStatus.Capability, ResumeInPlaceWire.Capability, HandoffWire.LocalProbeCapability, RecordingIpc.DisableCapability];

    /// <summary><see cref="Current"/>, with <see cref="RecordingIpc.Capability"/> while this daemon has recording on,
    /// whatever its server last said: only the frame's answer can say what it holds, will take, or can't yet decide, so a
    /// hook's hand-over has to reach it.</summary>
    public static IReadOnlyList<string> Advertised(bool recording) => recording ? [.. Current, RecordingIpc.Capability] : Current;
}
