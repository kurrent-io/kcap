namespace Capacitor.App.Services.CliAdoption;

/// <summary>How a <see cref="PrivilegedSwap"/> run ended. <see cref="PutBack"/> means the helper reported the links put
/// back with every entry in place; <see cref="Cancelled"/>, that it never reported its pid and so changed nothing,
/// whether the prompt was dismissed, authentication failed, osascript did not run or the helper refused its status
/// folder; <see cref="CutShort"/>, that it reported its pid but neither the deletion finished nor the put-back.</summary>
public enum PrivilegedResult { Deleted, PutBack, Cancelled, CutShort }
