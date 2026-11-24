using System.Globalization;
using System.Text;

namespace Capacitor.App.ViewModels;

internal sealed record ChatTurnDestination(ChatItemViewModel Row, int ProjectedIndex, int Number, bool Folded, bool Partial) {
    public string Title => Row switch {
        UserTurnItem { IsUndelivered: true } => "Undelivered prompt",
        UserTurnItem => string.Create(CultureInfo.CurrentCulture, $"Turn {Number}"),
        ShellCommandItem { HasCommand: true } => "Shell command",
        SystemNoteItem => "Session event",
        _ when Partial => "Partial turn",
        _ => "Ungrouped output",
    };

    public string Detail {
        get {
            var text = Row switch {
                UserTurnItem user => Snippet(user.Text, user.HasAttachments ? "Attachments" : "Empty prompt"),
                AssistantTextItem reply => Snippet(reply.Text, "Assistant reply"),
                _ when Partial => "The prompt is outside the loaded history.",
                _ => "",
            };
            return Folded ? (text.Length == 0 ? "Folded topic" : $"Folded topic · {text}") : text;
        }
    }

    static string Snippet(string text, string fallback) {
        const int limit = 100;
        var result = new StringBuilder(limit);
        var space = false;
        // Bound work even for a large paste with no visible characters near its start.
        foreach (var rune in text.AsSpan(0, Math.Min(text.Length, limit * 4)).EnumerateRunes()) {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune)) {
                space = result.Length > 0;
                continue;
            }
            if (result.Length + (space ? 1 : 0) + rune.Utf16SequenceLength > limit) {
                result.Append('…');
                break;
            }
            if (space) result.Append(' ');
            space = false;
            result.Append(rune.ToString());
        }
        return result.Length > 0 ? result.ToString() : fallback;
    }
}
