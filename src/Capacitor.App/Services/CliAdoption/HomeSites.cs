namespace Capacitor.App.Services.CliAdoption;

/// <summary>What the privileged helper reads below every account's home for a reference to the install:
/// <paramref name="Files"/> relative to the home, and, in the account registry at <paramref name="Registry"/> relative to
/// it, each harness directory listed, read for <paramref name="AccountFiles"/> relative to that directory.</summary>
public sealed record HomeSites(IReadOnlyList<string> Files, string? Registry, IReadOnlyList<string> AccountFiles) {
    public static HomeSites None { get; } = new([], null, []);
}
