namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>What the local server's background intelligence is set to, as <c>GET /api/admin/intelligence</c> answers
/// it; snake_case on the wire.</summary>
/// <param name="Mode">One of <see cref="IntelligenceModes"/>; <c>custom</c> carries neither harness nor vendor.</param>
/// <param name="Model">A provider's effective model.</param>
/// <param name="ImportOutcomesPinned">A source above the server's settings store supplies it, so no write changes it.</param>
/// <param name="BacklogSessionTouchesPerDay">How many older sessions the background work takes on a day.</param>
public sealed record IntelligenceSettings(
    string Mode, string? Harness, string? Vendor, string? Model, bool ImportOutcomes, bool ImportOutcomesPinned,
    int BacklogSessionTouchesPerDay, bool BacklogLimitPinned, IReadOnlyList<string> Resolving) {
    public const int MinDailyLimit = 1;
    public const int MaxDailyLimit = 100_000;
    public const string DailyLimitRange = "1 to 100,000";

    public static bool IsDailyLimit(int value) => value is >= MinDailyLimit and <= MaxDailyLimit;
}
