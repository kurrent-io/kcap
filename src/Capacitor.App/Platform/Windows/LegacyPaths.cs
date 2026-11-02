using System.Text;

namespace Capacitor.App.Platform.Windows;

/// Path tests for retiring a legacy Windows install. Windows paths compare without case; a mention in stored text
/// counts only when the root ends there, so <c>KurrentCapacitor</c> never matches <c>KurrentCapacitor2</c>.
public static class LegacyPaths {
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// A blank side is never the same path: <see cref="Path.GetFullPath(string)"/> throws on one, and a registry value
    /// can be stored empty.
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <paramref name="root"/> itself or anything inside it.
    public static bool Within(string path, string root) {
        var p = Normalize(path);
        var r = Normalize(root);
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// Whether <paramref name="text"/>, a registry value or a command line, names <paramref name="root"/> or a path in it.
    public static bool Mentions(string? text, string root) => text is not null && Occurrences(text, Trim(root)).Any();

    /// <paramref name="text"/> with every mention of <paramref name="from"/> naming <paramref name="to"/> instead.
    public static string Rebase(string text, string from, string to) {
        var needle = Trim(from);
        var sb     = new StringBuilder();
        var last   = 0;
        foreach (var at in Occurrences(text, needle)) {
            sb.Append(text, last, at - last).Append(Trim(to));
            last = at + needle.Length;
        }
        return sb.Append(text, last, text.Length - last).ToString();
    }

    /// <summary>Whether a shortcut file names <paramref name="root"/>. A .lnk keeps its target and working folder as
    /// UTF-16 text and as ANSI text, at offsets with no fixed alignment, so all three readings are searched.</summary>
    public static bool ShortcutMentions(byte[] lnk, string root) =>
        Mentions(Encoding.Unicode.GetString(lnk), root)
        || (lnk.Length > 1 && Mentions(Encoding.Unicode.GetString(lnk, 1, lnk.Length - 1), root))
        || Mentions(Encoding.Latin1.GetString(lnk), root);

    static string Trim(string root) => root.TrimEnd('\\', '/');

    static IEnumerable<int> Occurrences(string text, string needle) {
        if (needle.Length == 0) yield break;
        for (var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.OrdinalIgnoreCase)) {
            var end = at + needle.Length;
            if (end == text.Length || text[end] is '\\' or '/' or '"' or '\0' || char.IsWhiteSpace(text[end])) yield return at;
        }
    }
}
