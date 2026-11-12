using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// A heading's text with its casing chosen by style: capitals while Upper is true (the classic look),
/// otherwise as written, with an all-capitals source set in sentence case.
public static class Eyebrow {
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(Eyebrow));

    public static readonly AttachedProperty<bool> UpperProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("Upper", typeof(Eyebrow), defaultValue: true);

    static Eyebrow() {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Update(block));
        UpperProperty.Changed.AddClassHandler<TextBlock>((block, _) => Update(block));
    }

    public static string? GetText(TextBlock block) => block.GetValue(TextProperty);
    public static void SetText(TextBlock block, string? value) => block.SetValue(TextProperty, value);
    public static bool GetUpper(TextBlock block) => block.GetValue(UpperProperty);
    public static void SetUpper(TextBlock block, bool value) => block.SetValue(UpperProperty, value);

    static void Update(TextBlock block) {
        if (GetText(block) is not { Length: > 0 } text) return;
        block.Text = GetUpper(block) ? text.ToUpperInvariant()
            : !text.Any(char.IsLower) ? char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant()
            : text;
    }
}
