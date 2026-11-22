using System.Globalization;

namespace Capacitor.App.ViewModels;

/// Where a message came from in its transcript: the line's position in the feed (an event number
/// remotely, a file offset locally) and which message of that line it is. Unlike a count of the
/// messages shown, it holds while older pages load above.
public readonly record struct ChatMessageKey(long Line, int Index) : IComparable<ChatMessageKey> {
    public int CompareTo(ChatMessageKey other) => Line != other.Line ? Line.CompareTo(other.Line) : Index.CompareTo(other.Index);

    public static bool operator <(ChatMessageKey a, ChatMessageKey b) => a.CompareTo(b) < 0;
    public static bool operator >(ChatMessageKey a, ChatMessageKey b) => a.CompareTo(b) > 0;
    public static bool operator <=(ChatMessageKey a, ChatMessageKey b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ChatMessageKey a, ChatMessageKey b) => a.CompareTo(b) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Line}.{Index}");

    public static ChatMessageKey? Parse(string? text) {
        var dot = text?.IndexOf('.') ?? -1;
        return dot > 0
            && long.TryParse(text.AsSpan(0, dot), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line)
            && int.TryParse(text.AsSpan(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? new ChatMessageKey(line, index) : null;
    }
}
