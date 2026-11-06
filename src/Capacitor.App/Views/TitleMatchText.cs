using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Renders a TextBlock's text with each occurrence of the filter SemiBold in KcapInfoBrush, the
/// rail filter's own colour; the rest keeps the block's foreground.
public static class TitleMatchText {
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(TitleMatchText));

    public static readonly AttachedProperty<string?> FilterProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Filter", typeof(TitleMatchText));

    static TitleMatchText() {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Render(block));
        FilterProperty.Changed.AddClassHandler<TextBlock>((block, _) => Render(block));
    }

    public static string? GetText(TextBlock block) => block.GetValue(TextProperty);
    public static void SetText(TextBlock block, string? value) => block.SetValue(TextProperty, value);
    public static string? GetFilter(TextBlock block) => block.GetValue(FilterProperty);
    public static void SetFilter(TextBlock block, string? value) => block.SetValue(FilterProperty, value);

    /// Text always carries the whole title, for anything that reads it; inlines exist only while
    /// something matches, and the block renders them in preference to Text.
    static void Render(TextBlock block) {
        var text = GetText(block);
        var runs = TitleMatch.Split(text, GetFilter(block));
        block.Inlines = null;
        block.Text = text;
        if (!runs.Any(run => run.Matched)) return;
        var inlines = new InlineCollection();
        foreach (var run in runs) {
            var inline = new Run(run.Text);
            if (run.Matched) {
                inline.FontWeight = FontWeight.SemiBold;
                if (MatchBrush(block) is { } brush) inline.Foreground = brush;
            }
            inlines.Add(inline);
        }
        block.Inlines = inlines;
    }

    static IBrush? MatchBrush(TextBlock block) =>
        block.TryFindResource("KcapInfoBrush", out var found) || Application.Current?.TryFindResource("KcapInfoBrush", out found) == true
            ? found as IBrush
            : null;
}
