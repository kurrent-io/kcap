namespace Capacitor.App.Services.CliAdoption;

public sealed record ProcessFact(int Pid, string? Executable, IReadOnlyList<string>? Args, DateTime StartedUtc);
