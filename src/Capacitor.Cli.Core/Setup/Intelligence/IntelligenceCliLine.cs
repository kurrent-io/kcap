using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>The one line <c>kcap local intelligence --json</c> prints, which the desktop app reads: an applied answer's
/// fields, or <see cref="Settings"/> for a status read or a preference write. Null members are left out.</summary>
public sealed record IntelligenceCliLine(
    bool Ok, string? Mode = null, string? Harness = null, string? Vendor = null, bool? ImportOutcomes = null,
    int? BacklogSessionTouchesPerDay = null, IReadOnlyList<string>? Resolving = null, string? Error = null,
    IntelligenceSettings? Settings = null) {
    public static IntelligenceCliLine From(IntelligenceSetupResponse r) =>
        new(true, r.Mode, r.Harness, r.Vendor, r.ImportOutcomes, r.BacklogSessionTouchesPerDay, r.Resolving);

    /// <summary>The applied answer, or null for a refusal or a line missing what an applied one carries.</summary>
    [JsonIgnore]
    public IntelligenceSetupResponse? Applied =>
        Ok && Mode is not null && ImportOutcomes is { } outcomes && BacklogSessionTouchesPerDay is { } perDay && Resolving is not null
            ? new(Mode, Harness, Vendor, outcomes, perDay, Resolving)
            : null;

    public static IntelligenceCliLine From(IntelligenceSettings settings) => new(true, Settings: settings);

    /// <summary>The settings a read or a preference write answered, or null for a refusal or a line without them.</summary>
    [JsonIgnore]
    public IntelligenceSettings? ReadSettings => Ok && Settings is { Mode: not null, Resolving: not null } settings ? settings : null;
}
