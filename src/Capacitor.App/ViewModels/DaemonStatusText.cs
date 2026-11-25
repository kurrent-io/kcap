using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

public static class DaemonStatusText {
    public static string Attach(AttachStatus status, DaemonInfoDto? daemon, bool preview = false) => status.State switch {
        AttachState.Connected  => WithName(preview ? "connected" : "running", daemon?.Name),
        AttachState.Connecting => "connecting",
        _ => status.Reason switch {
            "daemon_unreachable"         => "not running",
            "daemon_incompatible"        => "version doesn't match the app",
            ExpectedDaemonRestart.Reason => "restarting",
            ExpectedDaemonRestart.StartingReason => "starting",
            _                            => "unreachable",
        },
    };

    static string WithName(string state, string? name) => name is { Length: > 0 } ? $"{state} · {name}" : state;

    internal static StatusGlyphKind AttachGlyph(AttachStatus status) => status.State switch {
        AttachState.Connected => StatusGlyphKind.Healthy,
        AttachState.Connecting => StatusGlyphKind.Activity,
        _ => status.Reason switch {
            ExpectedDaemonRestart.Reason or ExpectedDaemonRestart.StartingReason => StatusGlyphKind.Activity,
            "daemon_unreachable" => StatusGlyphKind.Stopped,
            "daemon_incompatible" => StatusGlyphKind.Attention,
            _ => StatusGlyphKind.Failure,
        },
    };

    internal static StatusGlyphKind? ServerGlyph(AttachStatus status, DaemonInfoDto? daemon) => Connection(status, daemon) switch {
        "connected" => StatusGlyphKind.Healthy,
        "connecting" or "reconnecting" => StatusGlyphKind.Activity,
        "disconnected" or "signed out" or "sign-in rejected" => StatusGlyphKind.Attention,
        null => null,
        _ => StatusGlyphKind.Unknown,
    };

    public static string? AttachHint(AttachStatus status) => status.State switch {
        AttachState.Unreachable => status.Reason switch {
            "daemon_unreachable"         => "Start it from the main window, or run kcap daemon start.",
            "daemon_incompatible"        => "Update kcap so the app and the daemon are the same version.",
            ExpectedDaemonRestart.Reason or ExpectedDaemonRestart.StartingReason => null,
            _                            => "Reconnect from the main window. If it stays unreachable, restart the daemon.",
        },
        _ => null,
    };

    /// The daemon's own connection word, its sign-in named when that is what keeps it off the server. Null while not
    /// attached: a snapshot kept from before says nothing about a daemon the app cannot reach.
    public static string? Connection(AttachStatus status, DaemonInfoDto? daemon) =>
        status.State != AttachState.Connected || daemon is null ? null
        : daemon.Connection != "connected" && daemon.Credential is { } credential
            ? credential == "rejected" ? "sign-in rejected" : "signed out"
        : daemon.Connection;

    public static string? Server(AttachStatus status, DaemonInfoDto? daemon) =>
        Connection(status, daemon) is not { } word ? null
        : string.IsNullOrEmpty(daemon!.ServerUrl) ? word
        : $"{word} · {daemon.ServerUrl}";

    public static string? ServerHint(AttachStatus status, DaemonInfoDto? daemon) => Connection(status, daemon) switch {
        "signed out" or "sign-in rejected" => "Sign in to the daemon's profile under Profiles.",
        "disconnected" or "reconnecting"   => "The daemon keeps retrying. Check that the server is running and reachable.",
        _                                  => null,
    };
}
