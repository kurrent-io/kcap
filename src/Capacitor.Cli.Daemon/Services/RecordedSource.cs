using System.Text.Json.Serialization;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>A source this daemon records, as its persisted list holds it.</summary>
/// <param name="AgentId">The hosted agent whose exit ends the source.</param>
/// <param name="SessionId">Spelled as hooks spell it, or a claim reads as nobody's and a watcher starts beside the
/// recorder.</param>
/// <param name="SubagentId">Null for the session's own transcript.</param>
/// <param name="Drained">It made its final drain at a graceful stop, so a next boot that lets it go has lost nothing.</param>
internal sealed record RecordedSource(string AgentId, string SessionId, string? SubagentId, string TranscriptPath, string Vendor, string? Cwd, bool SkipTitle, bool Drained = false) {
    [JsonIgnore] public RecordingSource Recording => new(SessionId, SubagentId, TranscriptPath, Vendor, Cwd);

    [JsonIgnore] public string Key => Recording.Key;
}
