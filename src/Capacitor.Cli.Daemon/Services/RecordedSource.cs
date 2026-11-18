using System.Text.Json.Serialization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>A source this daemon records, as its persisted list holds it.</summary>
/// <param name="AgentId">The hosted agent whose exit ends the source; null for a terminal session's.</param>
/// <param name="SessionId">Spelled as hooks spell it, or a claim reads as nobody's and a watcher starts beside the
/// recorder.</param>
/// <param name="SubagentId">Null for the session's own transcript.</param>
/// <param name="Drained">It made its final drain at a graceful stop, so a next boot that lets it go has lost nothing.</param>
/// <param name="Process">The terminal process whose exit ends a terminal session's source, which has no agent to end it.</param>
internal sealed record RecordedSource(
    string? AgentId, string SessionId, string? SubagentId, string TranscriptPath, string Vendor, string? Cwd, bool SkipTitle, bool Drained = false,
    ProcessIncarnation? Process = null) {
    [JsonIgnore] public RecordingSource Recording => new(SessionId, SubagentId, TranscriptPath, Vendor, Cwd);

    [JsonIgnore] public string Key => Recording.Key;

    /// <summary>Its session runs in a terminal, so the hosted catch-up that leaves a stale hosted claim alone never comes.</summary>
    [JsonIgnore] public bool IsTerminal => AgentId is null;
}
