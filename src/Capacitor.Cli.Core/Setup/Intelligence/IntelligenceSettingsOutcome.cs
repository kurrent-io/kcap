namespace Capacitor.Cli.Core.Setup.Intelligence;

public abstract record IntelligenceSettingsOutcome {
    IntelligenceSettingsOutcome() { }

    public sealed record Read(IntelligenceSettings Settings) : IntelligenceSettingsOutcome;

    /// <summary>The server refused and stored nothing; <paramref name="Error"/> is its code, or
    /// <see cref="IntelligenceSettingsClient.NeedsUpdate"/> for a server without the route.</summary>
    public sealed record Refused(string Error) : IntelligenceSettingsOutcome;

    /// <summary>The request may not have reached the server.</summary>
    public sealed record Failed(string Problem) : IntelligenceSettingsOutcome;
}
