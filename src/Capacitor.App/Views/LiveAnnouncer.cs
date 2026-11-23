using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// Speaks a short status ("Copied", "Send failed") through the screen reader without moving focus.
/// The window holds two off-screen live regions, PoliteAnnouncer and AssertiveAnnouncer; a window
/// without them announces nothing.
public static class LiveAnnouncer {
    public const string PoliteName = "PoliteAnnouncer";
    public const string AssertiveName = "AssertiveAnnouncer";

    public static void Polite(Visual from, string text) => Announce(from, text, PoliteName);

    public static void Assertive(Visual from, string text) => Announce(from, text, AssertiveName);

    static void Announce(Visual from, string text, string region) {
        if (TopLevel.GetTopLevel(from) is not Window window || NameScope.GetNameScope(window)?.Find<TextBlock>(region) is not { } live) return;
        // Cleared first, so the same words twice in a row are still a change the reader hears.
        live.Text = "";
        live.Text = text;
    }
}
