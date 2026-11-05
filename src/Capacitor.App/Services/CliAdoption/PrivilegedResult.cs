namespace Capacitor.App.Services.CliAdoption;

/// <summary>How a <see cref="PrivilegedSwap"/> run ended: the entries deleted, the links put back with every entry in
/// place, the administrator prompt cancelled, or go sent without the deletion reported finished.</summary>
public enum PrivilegedResult { Deleted, PutBack, Cancelled, CutShort }
