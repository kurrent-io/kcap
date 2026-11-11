using Avalonia.Media;

namespace Capacitor.App.Views;

/// Commands keep the program prefix and target tail. Paths give the available width to the
/// trailing folders and filename, which identify the target even in a narrow row.
public static class ChatTrimming {
    public static TextTrimming MiddleEllipsis { get; } = new TextLeadingPrefixTrimming("…", 24);
    public static TextTrimming PathEllipsis { get; } = TextTrimming.LeadingCharacterEllipsis;
}
