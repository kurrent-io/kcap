namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>The answer on <see cref="FrameType.DisableSessionAck"/>.</summary>
/// <param name="Status"><see cref="RecordingIpc.Stopped"/>, <see cref="RecordingIpc.SendInFlight"/> or
/// <see cref="RecordingIpc.NotHeld"/>.</param>
/// <param name="AgentLive">See <see cref="RecordingDisableAnswer.AgentLive"/>.</param>
/// <param name="Server">See <see cref="RecordingDisableAnswer.Server"/>.</param>
/// <param name="MarkFailed">See <see cref="RecordingDisableAnswer.MarkFailed"/>. An older daemon leaves it out.</param>
public sealed record DisableSessionAckDto(string Status, bool AgentLive, string? Server, bool MarkFailed = false);
