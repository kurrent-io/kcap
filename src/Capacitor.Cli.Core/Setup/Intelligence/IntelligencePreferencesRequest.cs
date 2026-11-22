namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>The body of the server's <c>PUT /api/admin/intelligence/preferences</c>; a null member leaves that
/// setting as it is.</summary>
public sealed record IntelligencePreferencesRequest(bool? ImportOutcomes, int? BacklogSessionTouchesPerDay);
