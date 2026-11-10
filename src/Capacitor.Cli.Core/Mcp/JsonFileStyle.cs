using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Mcp;

/// <summary>How a JSON config file is laid out, so a rewrite keeps the user's own: BOM, compact or indented
/// (width and character), newline sequence and trailing newline. A file in a form System.Text.Json writes
/// round-trips byte for byte; a hand-formatted one keeps all of that but its exact spacing.</summary>
sealed record JsonFileStyle(bool Bom, bool Indented, int IndentSize, char IndentCharacter, string NewLine, bool TrailingNewline) {
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    static ReadOnlySpan<byte> Preamble => [0xEF, 0xBB, 0xBF];

    /// <summary>A file kcap creates: two-space indent, no BOM, no trailing newline.</summary>
    public static JsonFileStyle New { get; } = new(false, true, 2, ' ', Environment.NewLine, false);

    public static (string Text, JsonFileStyle Style) Read(string path) {
        var bytes = File.ReadAllBytes(path);
        var bom   = bytes.AsSpan().StartsWith(Preamble);
        var text  = Utf8.GetString(bom ? bytes.AsSpan(Preamble.Length) : bytes);

        return (text, Detect(text, bom));
    }

    public static JsonFileStyle Detect(string text, bool bom) {
        var body = text.Trim();

        if (body.Length == 0) return New with { Bom = bom };

        var newLine  = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\n') ? "\n" : New.NewLine;
        var trailing = text.EndsWith('\n');

        if (!body.Contains('\n')) return new(bom, false, New.IndentSize, New.IndentCharacter, newLine, trailing);

        foreach (var line in body.Split('\n').Skip(1)) {
            if (line.Length == 0 || line[0] is not (' ' or '\t')) continue;

            var size = 0;
            while (size < line.Length && line[size] == line[0]) size++;

            // System.Text.Json takes an indent of at most 127.
            return new(bom, true, Math.Min(size, 127), line[0], newLine, trailing);
        }

        return New with { Bom = bom, NewLine = newLine, TrailingNewline = trailing };
    }

    public byte[] Serialize(JsonNode root) {
        using var buffer = new MemoryStream();

        if (Bom) buffer.Write(Preamble);

        // Relaxed escaping keeps `&`, `'` and non-ASCII text as written rather than as \u escapes; this is a
        // config file, never HTML.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions {
                   Indented         = Indented,
                   IndentSize       = IndentSize,
                   IndentCharacter  = IndentCharacter,
                   NewLine          = NewLine,
                   Encoder          = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
            root.WriteTo(writer);

        if (TrailingNewline) buffer.Write(Utf8.GetBytes(NewLine));

        return buffer.ToArray();
    }
}
