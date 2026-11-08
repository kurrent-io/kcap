namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>The daemon's answer to <see cref="FrameType.SteeringSet"/>. <see cref="Steering"/> is the switch in effect
/// afterwards, on success and refusal alike; null only when no daemon answered.</summary>
public sealed record SteeringSetAckDto(bool Ok, string? Reason, string? Steering);
