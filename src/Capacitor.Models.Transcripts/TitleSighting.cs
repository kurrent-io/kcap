namespace Capacitor.Models.Transcripts;

/// <summary>A harness title a line carries. <paramref name="Kind"/> is <c>auto</c> or <c>rename</c>.
/// <paramref name="OwnTime"/> is set only when the line records when the title changed. A
/// <paramref name="Legacy"/> title is Claude's <c>summary</c> shape, the lowest-ranked harness title.</summary>
public sealed record TitleSighting(string Kind, string Title, DateTimeOffset? OwnTime, bool Legacy);
