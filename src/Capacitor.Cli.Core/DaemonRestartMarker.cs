using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>
/// The <c>&lt;name&gt;.restart-pending</c> marker the daemon writes when a
/// restart-after-update is queued and clears on (successor) startup. Read by
/// <c>kcap daemon status</c> and the desktop app — same on-disk pattern as the PID
/// file, so neither needs a socket round-trip. JSON via <see cref="JsonObject"/>
/// to stay NativeAOT-safe (no reflection serializer).
/// </summary>
public sealed record DaemonRestartMarker(string RunningVersion, string Reason, DateTimeOffset QueuedAt) {
    /// <summary>The daemon saw its own binary change.</summary>
    public const string SelfDetected = "self-detected";

    /// <summary>A client asked for a bare restart that has not fired: one that failed to start and is retried, or a
    /// foreground daemon's. A daemon that predates <see cref="WhenIdle"/> writes it for a when-idle request too.</summary>
    public const string Requested = "requested";

    /// <summary>A client asked for a restart at the next turn boundary.</summary>
    public const string TurnBoundary = "turn-boundary";

    /// <summary>A client asked for a restart once the daemon has no agents at all, idle ones included.</summary>
    public const string WhenIdle = "when-idle";

    /// <summary>Whether the restart waits only for the interactive agents' turns, up to the daemon's turn wait.</summary>
    public bool WaitsForTurns => Reason is SelfDetected or TurnBoundary;

    /// <summary>Whether the restart waits for the daemon to have no agents. False for a <see cref="Requested"/>
    /// marker, which cannot say.</summary>
    public bool WaitsForNoAgents => Reason is WhenIdle;

    public static void Write(DaemonStore store, string daemonName, DaemonRestartMarker m) {
        store.EnsureDirectory();
        var obj = new JsonObject {
            ["running_version"] = m.RunningVersion,
            ["reason"]          = m.Reason,
            ["queued_at"]       = m.QueuedAt,
        };
        var path = store.RestartPendingPath(daemonName);
        var tmp  = $"{path}.tmp";
        File.WriteAllText(tmp, obj.ToJsonString());
        File.Move(tmp, path, overwrite: true);
    }

    public static DaemonRestartMarker? TryRead(DaemonStore store, string daemonName) {
        var path = store.RestartPendingPath(daemonName);
        if (!File.Exists(path)) return null;
        try {
            var node = JsonNode.Parse(File.ReadAllText(path));
            var ver  = node?["running_version"]?.GetValue<string>();
            var why  = node?["reason"]?.GetValue<string>() ?? Requested;
            var when = node?["queued_at"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UnixEpoch;
            return ver is null ? null : new DaemonRestartMarker(ver, why, when);
        } catch {
            return null; // corrupt marker — treat as absent
        }
    }

    public static void Delete(DaemonStore store, string daemonName) {
        try { File.Delete(store.RestartPendingPath(daemonName)); } catch { /* best-effort */ }
    }

    /// <summary>One-line status text for <c>kcap daemon status</c>.</summary>
    public string Describe() =>
        $"restart pending: running {RunningVersion}, "
      + Reason switch {
            SelfDetected => "newer binary detected on disk",
            TurnBoundary => "restart requested at the next turn boundary",
            WhenIdle     => "restart requested once no agents are left",
            _            => "restart requested",
        }
      + $" (queued {QueuedAt.LocalDateTime:yyyy-MM-dd HH:mm}, {Reason})";
}
