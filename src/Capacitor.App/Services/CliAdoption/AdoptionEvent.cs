namespace Capacitor.App.Services.CliAdoption;

/// <summary>The fields every takeover event in the lifecycle log carries about one install.</summary>
internal static class AdoptionEvent {
    public static (string Key, string? Value)[] For(OtherCliInstall install, string? result = null, string? reason = null) =>
        [("install", install.Root), ("version", install.Version), ("result", result), ("reason", reason)];
}
