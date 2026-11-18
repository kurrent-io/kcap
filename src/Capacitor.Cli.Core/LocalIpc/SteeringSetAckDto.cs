namespace Capacitor.Cli.Core.LocalIpc;

/// <summary>The daemon's answer to <see cref="FrameType.SteeringSet"/>. <see cref="Steering"/> is the switch in effect
/// afterwards, on success and refusal alike; null only when no daemon answered. <see cref="Detail"/> says why a refusal
/// happened, in the daemon's words, when it has more to say than <see cref="Reason"/>.</summary>
public sealed record SteeringSetAckDto(bool Ok, string? Reason, string? Steering, string? Detail = null);
