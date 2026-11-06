using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// <param name="Agents">"keep" leaves hosted agents running for the next daemon; "end" stops them.</param>
/// <param name="Exit">"relaunch" exits for a supervisor to restart the daemon; "final" exits for good.</param>
/// <param name="WhenIdle">True refuses with a <c>busy:</c> error while an eval or a launch in admission is
/// in flight, rather than stopping; absent from a client that predates it.</param>
/// <param name="AtTurnBoundary">True holds a final exit until every interactive agent is at a turn boundary or the
/// daemon's turn wait runs out: the daemon answers <see cref="ShutdownIpc.QueuedFor"/> first and <c>stopping</c> once
/// it stops, and closing the connection in between withdraws the stop. A daemon that predates it stops at once.</param>
public sealed record ShutdownRequestDto(string Agents, string Exit, bool WhenIdle = false, bool AtTurnBoundary = false);

public static class ShutdownIpc {
    /// The capability a daemon that honours <see cref="ShutdownRequestDto.AtTurnBoundary"/> advertises.
    public const string TurnBoundaryCapability = "shutdown/2";

    const string QueuedPrefix = "queued:";

    // A turn wait past this is a misconfiguration; capping it keeps a client's reply timer constructible.
    static readonly TimeSpan MaxQueuedWait = TimeSpan.FromDays(1);

    public static string Serialize(ShutdownRequestDto dto) =>
        JsonSerializer.Serialize(dto, ShutdownIpcJsonContext.Default.ShutdownRequestDto);

    /// Null for malformed JSON or an agents/exit value outside the allowed sets.
    public static ShutdownRequestDto? Parse(string text) {
        ShutdownRequestDto? dto;
        try {
            dto = JsonSerializer.Deserialize(text, ShutdownIpcJsonContext.Default.ShutdownRequestDto);
        } catch (JsonException) {
            return null;
        }

        return dto is { Agents: "keep" or "end", Exit: "relaunch" or "final" } ? dto : null;
    }

    /// The ShutdownAck text of a stop held for a turn boundary, naming how long the daemon holds it at most.
    public static string QueuedFor(TimeSpan wait) =>
        QueuedPrefix + ((long)Math.Ceiling(Math.Max(0, wait.TotalSeconds))).ToString(CultureInfo.InvariantCulture);

    public static bool TryParseQueued(string text, out TimeSpan wait) {
        wait = TimeSpan.Zero;
        if (!text.StartsWith(QueuedPrefix, StringComparison.Ordinal)) return false;
        if (!long.TryParse(text.AsSpan(QueuedPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;

        wait = seconds >= MaxQueuedWait.TotalSeconds ? MaxQueuedWait : TimeSpan.FromSeconds(seconds);
        return true;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ShutdownRequestDto))]
public partial class ShutdownIpcJsonContext : JsonSerializerContext;
