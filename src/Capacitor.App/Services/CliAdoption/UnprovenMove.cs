namespace Capacitor.App.Services.CliAdoption;

/// <summary>A repoint that ran for the install at <paramref name="InstallRoot"/> without the move being proven. The
/// install is kept until a later run proves each of <paramref name="Units"/> moved, judged by what it was before that
/// repoint: one that ran must run the bundle's daemon again. <paramref name="Daemon"/> is the one that did not.</summary>
public sealed record UnprovenMove(string InstallRoot, string? Daemon, IReadOnlyList<ServiceUnitFact> Units);
