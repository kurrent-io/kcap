using System.Globalization;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>Whether the server is storing session vectors: a usable model says nothing about that.</summary>
public sealed record EmbeddingVectors(VectorStorageState State, DateTimeOffset? LastStoredAt = null, DateTimeOffset? LastFailureAt = null) {
    /// <summary>Null when the report carries no <c>vectors</c> object, as an older server's does.</summary>
    public static EmbeddingVectors? From(JsonElement report) =>
        report.Obj("vectors") is { } vectors
            ? new(VectorStorageStateExtensions.FromWire(vectors.Str("state")), Time(vectors, "last_stored_at"), Time(vectors, "last_failure_at"))
            : null;

    static DateTimeOffset? Time(JsonElement vectors, string property) =>
        vectors.Str(property) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
}
