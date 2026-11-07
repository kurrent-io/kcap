namespace Capacitor.Cli.Daemon.Services;

/// <summary>What the capture scope leaves this daemon to do on its own.</summary>
/// <param name="LeftToHooks">A hook's capture gate decides what this daemon may record, so it takes nothing ahead of one.</param>
/// <param name="MayImport">Whether a source in this working directory may be marked for import. An import uploads the
/// transcript, so a source the scope refuses stays off the server that way too.</param>
internal sealed record RecordingScope(bool LeftToHooks, Func<string?, bool> MayImport) {
    public static readonly RecordingScope Open = new(false, static _ => true);

    /// <summary>A config that can't be read may exclude anything: unknown lists are not absent lists.</summary>
    public static readonly RecordingScope Unknown = new(true, static _ => false);
}
