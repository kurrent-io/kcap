namespace Capacitor.App.NeedToKnow;

/// The need-to-know basis as kept in app state. The displays are names read leniently, so a value this
/// build does not know falls back to its default instead of failing the whole state file.
public sealed record NeedToKnowPreferences(string? Detail, string? Footnote, double? DetailFade, double? FootnoteFade);
