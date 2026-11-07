namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>The answer on <see cref="FrameType.DisableSessionAck"/>.</summary>
/// <param name="Status"><see cref="RecordingIpc.Stopped"/>, <see cref="RecordingIpc.SendInFlight"/> or
/// <see cref="RecordingIpc.NotHeld"/>.</param>
/// <param name="TerminalLive">See <see cref="RecordingDisableAnswer.TerminalLive"/>.</param>
/// <param name="Server">See <see cref="RecordingDisableAnswer.Server"/>.</param>
public sealed record DisableSessionAckDto(string Status, bool TerminalLive, string? Server);
