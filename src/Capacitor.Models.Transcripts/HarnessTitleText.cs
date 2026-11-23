using System.Text;

namespace Capacitor.Models.Transcripts;

/// <summary>A harness title as the server stores it: whitespace runs collapsed to one space, capped at 200 characters,
/// and nothing for a title that is blank.</summary>
public static class HarnessTitleText {
    const int MaxLength = 200;

    public static string? Normalize(string? raw) {
        if (raw is null) return null;

        var sb     = new StringBuilder(Math.Min(raw.Length, MaxLength));
        var lastWs = true;

        foreach (var ch in raw) {
            if (sb.Length >= MaxLength) break;

            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) {
                if (!lastWs) sb.Append(' ');
                lastWs = true;
                continue;
            }

            sb.Append(ch);
            lastWs = false;
        }

        // A cut after a high surrogate would leave it unpaired.
        if (sb.Length >= MaxLength && char.IsHighSurrogate(sb[^1])) sb.Length--;

        var title = sb.ToString().TrimEnd();

        return title.Length == 0 ? null : title;
    }
}
