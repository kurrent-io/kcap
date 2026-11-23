namespace Capacitor.App.ViewModels;

/// One row of a status tooltip. Caption names the value and sits above it (Harness, Reason); Detail
/// adds lines under it (a grouped session's own facts).
public sealed record AgentStatusFact(string Text, string? Caption, string? Detail = null) {
    public bool HasCaption => Caption is { Length: > 0 };
    public bool HasDetail => Detail is { Length: > 0 };
}
