namespace Capacitor.App.Services;

/// The prompt to show and the local-data consent it captures.
/// <param name="HasLocalServer">Removing the settings then deletes its recorded sessions too, which the prompt's
/// warning says before the user agrees.</param>
public sealed record UninstallRequest(LifecyclePrompt Prompt, bool HasLocalServer) {
    /// <param name="removeSettings">The prompt's choice as it was answered.</param>
    internal UninstallMode Mode(bool removeSettings) => new(PurgeLocal: removeSettings && HasLocalServer, KeepConfig: !removeSettings);
}
