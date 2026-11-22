namespace Capacitor.App.Services;

/// A checkbox on a lifecycle prompt, unticked when the prompt opens. Ticking it swaps in the text for the wider
/// action, so the dialog only ever describes the answer it is about to take.
public sealed record LifecycleChoice(string Label, string Disclosure, string? Warning = null);
