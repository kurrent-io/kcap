using System.Text.Json.Serialization;

namespace Capacitor.App.Services;

/// An in-place resume of an ended session: the server reads the store it was recorded under itself.
public sealed record ResumeLaunchPayload(
    [property: JsonPropertyName("source_session_id")] string SourceSessionId,
    [property: JsonPropertyName("directory")]         string Directory);
