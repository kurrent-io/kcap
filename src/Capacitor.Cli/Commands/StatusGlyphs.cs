using System.Text;

namespace Capacitor.Cli.Commands;

/// <summary>Plain-text stand-ins for the marks the CLI prints. An encoder that cannot carry a mark
/// writes "?" for it, or a look-alike such as "." for an ellipsis, and then a wired and an unwired
/// harness read the same and a cut column looks like a full one.</summary>
static class StatusGlyphs {
    /// <summary>Outside WGL4, the repertoire the Windows console fonts are built to: Consolas and Lucida
    /// Console draw these as an empty box even when the stream carries them.</summary>
    static readonly (string Glyph, string Plain)[] Marks = [
        ("✓", "ok"),
        ("✗", "x"),
        ("⚠", "!"),
    ];

    static readonly (string Glyph, string Plain)[] Punctuation = [
        ("—", "-"),
        ("…", "..."),
        ("→", "->"),
        ("·", "-"),
        ("–", "-"),
    ];

    /// <summary>Whether <paramref name="encoding"/> is a Unicode encoding, the only kind that carries
    /// every mark.</summary>
    internal static bool Encodable(Encoding encoding) =>
        encoding.CodePage is 65001 or 1200 or 1201 or 12000 or 12001;

    /// <summary>Whether standard output carries every mark. A redirected Windows stream does not: it
    /// keeps the console's OEM code page.</summary>
    internal static bool ConsoleEncodable => Encodable(Console.OutputEncoding);

    internal static string Plain(string text) => Substitute(Substitute(text, Marks), Punctuation);

    /// <summary><paramref name="text"/> as standard output can carry it and its font can draw it.</summary>
    internal static string ForConsole(string text) => ForConsole(text, ConsoleEncodable, OperatingSystem.IsWindows());

    internal static string ForConsole(string text, bool encodable, bool windows) =>
        !encodable ? Plain(text) : windows ? Substitute(text, Marks) : text;

    /// <summary>The mark that ends a cut cell.</summary>
    internal static string Ellipsis(bool encodable) => encodable ? "…" : "...";

    /// <summary><paramref name="value"/> cut to at most <paramref name="width"/> characters, ending in
    /// <see cref="Ellipsis"/> when it was cut.</summary>
    internal static string Fit(string value, int width, bool encodable) {
        if (value.Length <= width) return value;

        var mark = Ellipsis(encodable);

        return width <= mark.Length ? value[..Math.Max(width, 0)] : value[..(width - mark.Length)] + mark;
    }

    static string Substitute(string text, (string Glyph, string Plain)[] substitutes) {
        foreach (var (glyph, plain) in substitutes) text = text.Replace(glyph, plain, StringComparison.Ordinal);

        return text;
    }
}
