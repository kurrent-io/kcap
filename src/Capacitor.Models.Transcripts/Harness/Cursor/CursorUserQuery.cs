using System.Globalization;
using System.Text.RegularExpressions;

namespace Capacitor.Models.Transcripts.Harness.Cursor;

/// Cursor's envelope around every user query it sends to the model and writes to its transcript: an
/// optional image list, an optional `&lt;timestamp&gt;` line, then a `&lt;user_query&gt;` block.
/// <code>
/// &lt;timestamp&gt;Wednesday, Jul 8, 2026, 9:48 AM (UTC-4)&lt;/timestamp&gt;
/// &lt;user_query&gt;
/// {the query the user typed}
/// &lt;/user_query&gt;
/// </code>
/// Only an envelope that is the whole turn is unwrapped, so a stray `&lt;user_query&gt;` in an ordinary prompt
/// stays byte for byte. The unwrap strips only Cursor's wrapper newline on each side, so the query still
/// equals the `beforeSubmitPrompt` hook text the attachments queue is keyed by.
public static partial class CursorUserQuery {
    const string TimestampOpen   = "<timestamp>";
    const string TimestampClose  = "</timestamp>";
    const string QueryOpen       = "<user_query>";
    const string QueryClose      = "</user_query>";
    const string ImageLine       = "[Image]";
    const string ImageFilesOpen  = "<image_files>";
    const string ImageFilesClose = "</image_files>";

    public readonly record struct Parsed(string Query, DateTimeOffset? Timestamp);

    public static Parsed Parse(string? text) {
        if (string.IsNullOrEmpty(text)) return new(string.Empty, null);

        return TryFindQuery(text, out var start, out var length, out var timestamp)
            ? new(text.Substring(start, length), timestamp)
            : new(text, null);
    }

    /// <inheritdoc cref="TryFindQuery(ReadOnlySpan{char}, out int, out int, out DateTimeOffset?)"/>
    public static bool TryFindQuery(string? text, out int start, out int length, out DateTimeOffset? timestamp) {
        start = 0;
        length = 0;
        timestamp = null;

        return !string.IsNullOrEmpty(text) && TryFindQuery(text.AsSpan(), out start, out length, out timestamp);
    }

    /// Where the query sits inside the text when the whole of it is Cursor's envelope.
    public static bool TryFindQuery(ReadOnlySpan<char> text, out int start, out int length, out DateTimeOffset? timestamp) {
        start = 0;
        length = 0;
        timestamp = null;

        if (text.IsEmpty) return false;

        // No outer trim: Cursor never pads the envelope, and a padded one must stay byte for byte.
        var rest = text;

        // An image ask leads with an [Image] line per image and the list of where Cursor saved them.
        if (rest.StartsWith(ImageLine) || rest.StartsWith(ImageFilesOpen)) {
            while (rest.StartsWith(ImageLine)) {
                var next = rest[ImageLine.Length..];
                if (!SkipNewline(ref next)) return false;
                rest = next;
            }

            if (!rest.StartsWith(ImageFilesOpen)) return false;

            var filesEnd = rest.IndexOf(ImageFilesClose);
            if (filesEnd < 0) return false;

            rest = rest[(filesEnd + ImageFilesClose.Length)..];
            if (!SkipNewline(ref rest)) return false;
        }

        DateTimeOffset? sent = null;

        if (rest.StartsWith(TimestampOpen)) {
            var close = rest.IndexOf(TimestampClose);
            if (close < 0) return false;
            sent = ParseTimestamp(rest[TimestampOpen.Length..close]);
            // Only Cursor's newline separator is skipped, so a padded one fails the query check below.
            rest = rest[(close + TimestampClose.Length)..];
            if (!SkipNewline(ref rest)) return false;

            // A prompt Cursor writes for itself, such as a background task's result, leaves a blank line.
            SkipNewline(ref rest);
        }

        if (!rest.StartsWith(QueryOpen) || !rest.EndsWith(QueryClose)) return false;

        // Every step above only trims the front, so the rest ends where the text does.
        var inner = rest[QueryOpen.Length..^QueryClose.Length];
        var (lead, trail) = WrapperNewlines(inner);

        start = text.Length - rest.Length + QueryOpen.Length + lead;
        length = inner.Length - lead - trail;
        timestamp = sent;

        return true;
    }

    static bool SkipNewline(ref ReadOnlySpan<char> rest) {
        if (rest.StartsWith("\r\n")) rest = rest[2..];
        else if (rest.StartsWith("\n")) rest = rest[1..];
        else return false;

        return true;
    }

    // Only Cursor's own newline on each side comes off; the user's indentation and trailing spaces stay.
    static (int Lead, int Trail) WrapperNewlines(ReadOnlySpan<char> inner) {
        var lead = inner.StartsWith("\r\n") ? 2 : inner.StartsWith("\n") ? 1 : 0;
        var rest = inner[lead..];
        var trail = rest.EndsWith("\r\n") ? 2 : rest.EndsWith("\n") ? 1 : 0;

        return (lead, trail);
    }

    static DateTimeOffset? ParseTimestamp(ReadOnlySpan<char> value) {
        var text = value.Trim().ToString();

        var offsetMatch = UtcOffset().Match(text);
        if (!offsetMatch.Success)
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var direct) ? direct : null;

        if (ParseOffset(offsetMatch.Groups["off"].Value) is not { } offset) return null;

        var datePart = text[..offsetMatch.Index].Trim();
        string[] formats = ["dddd, MMM d, yyyy, h:mm tt", "dddd, MMMM d, yyyy, h:mm tt"];

        if (!DateTime.TryParseExact(datePart, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return null;

        // An in-range match can still fail to construct.
        try {
            return new DateTimeOffset(dt, offset);
        } catch (ArgumentOutOfRangeException) {
            return null;
        }
    }

    // Null, never a throw, for an offset outside ±14:00 or off whole minutes: a bad timestamp must not
    // abort the line.
    static TimeSpan? ParseOffset(string raw) {
        if (string.IsNullOrEmpty(raw)) return TimeSpan.Zero;

        var negative = raw[0] == '-';
        var parts = raw.TrimStart('+', '-').Split(':');

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)) return null;

        var minutes = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return null;

        if (hours > 14 || minutes >= 60 || (hours == 14 && minutes != 0)) return null;

        var span = new TimeSpan(hours, minutes, 0);
        return negative ? -span : span;
    }

    // Anchored to the end, and the offset needs an explicit sign: "(UTC+5:30) garbage" and "(UTC5)" are not offsets.
    [GeneratedRegex(@"\(UTC(?<off>[+-]\d{1,2}(?::\d{2})?)?\)\s*$")]
    private static partial Regex UtcOffset();
}
