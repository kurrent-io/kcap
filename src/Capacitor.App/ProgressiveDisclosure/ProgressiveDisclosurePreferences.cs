namespace Capacitor.App.ProgressiveDisclosure;

/// Progressive disclosure as kept in app state. Missing reads as on.
public sealed record ProgressiveDisclosurePreferences(bool? Enabled);
