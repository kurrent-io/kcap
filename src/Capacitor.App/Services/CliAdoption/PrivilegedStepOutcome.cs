namespace Capacitor.App.Services.CliAdoption;

/// <summary>How a root-owned install's turn at the administrator prompt ended, as far as the rest of the run cares:
/// <see cref="WaitsOnPrompt"/> when a helper cut short left every entry in place, <see cref="Declined"/> when the user
/// said not now to the explanation or dismissed the prompt.</summary>
public enum PrivilegedStepOutcome { Settled, WaitsOnPrompt, Declined }
