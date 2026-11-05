namespace Capacitor.App.Services.CliAdoption;

/// <summary>How a <see cref="PrivilegedSwap"/> run ended. <see cref="PutBack"/> means the helper reported the links put
/// back with every entry in place; <see cref="Cancelled"/>, that it never reported its pid, whether the prompt was
/// dismissed, authentication failed or osascript did not run; <see cref="CutShort"/>, that it ran but reported neither
/// the deletion finished nor the put-back.</summary>
public enum PrivilegedResult { Deleted, PutBack, Cancelled, CutShort }
