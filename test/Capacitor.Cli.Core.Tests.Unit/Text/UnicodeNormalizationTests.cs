using System.Globalization;
using System.IO.Compression;
using System.Text;
using Capacitor.Cli.Core.Text;

namespace Capacitor.Cli.Core.Tests.Unit.Text;

public class UnicodeNormalizationTests {
    static readonly string Fixture = Path.Combine(
        AppContext.BaseDirectory, "fixtures", "unicode", $"NormalizationTest-{UnicodeNormalizationData.UnicodeVersion}.txt.gz");

    /// <summary>Every line of the Unicode conformance file, against the invariants the file defines
    /// for NFC and NFD; then every code point Part 1 does not list, which must be left alone.</summary>
    [Test]
    public async Task Conforms_to_the_unicode_normalization_test_file() {
        var failures = new List<string>();
        var listed   = new HashSet<int>();
        var part     = "";

        using var reader = new StreamReader(new GZipStream(File.OpenRead(Fixture), CompressionMode.Decompress));
        while (reader.ReadLine() is { } line) {
            if (line.StartsWith('#')) continue;
            if (line.StartsWith('@')) {
                part = line;
                continue;
            }

            var c = line.Split(';').Take(5).Select(Decode).ToArray();
            if (part == "@Part1") listed.Add(char.ConvertToUtf32(c[0], 0));

            Expect(line, "NFC", c[1], UnicodeNormalization.ToNfc, c[0], c[1], c[2]);
            Expect(line, "NFC", c[3], UnicodeNormalization.ToNfc, c[3], c[4]);
            Expect(line, "NFD", c[2], UnicodeNormalization.ToNfd, c[0], c[1], c[2]);
            Expect(line, "NFD", c[4], UnicodeNormalization.ToNfd, c[3], c[4]);
        }

        for (var cp = 0; cp <= 0x10FFFF; cp++) {
            if (cp is >= 0xD800 and <= 0xDFFF || listed.Contains(cp)) continue;
            var s = char.ConvertFromUtf32(cp);
            Expect($"U+{cp:X4}", "NFC", s, UnicodeNormalization.ToNfc, s);
            Expect($"U+{cp:X4}", "NFD", s, UnicodeNormalization.ToNfd, s);
        }

        await Assert.That(listed.Count).IsGreaterThan(10_000);
        await Assert.That(failures.Take(20)).IsEmpty();

        void Expect(string source, string form, string expected, Func<string, string> normalize, params string[] inputs) {
            foreach (var input in inputs) {
                var actual = normalize(input);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    failures.Add($"{source}: {form}({Hex(input)}) = {Hex(actual)}, expected {Hex(expected)}");
            }
        }
    }

    [Test]
    [Arguments("café", true)]
    [Arguments("café", false)]
    [Arguments("plain/ascii.txt", true)]
    [Arguments("Å", false)]
    [Arguments("\uD800 lone", true)]
    public async Task IsNfc_answers_whether_the_text_is_already_composed(string text, bool expected) {
        await Assert.That(UnicodeNormalization.IsNfc(text)).IsEqualTo(expected);
    }

    static string Decode(string field) {
        var sb = new StringBuilder();
        foreach (var hex in field.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            sb.Append(char.ConvertFromUtf32(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        return sb.ToString();
    }

    static string Hex(string s) {
        var parts = new List<string>();
        for (var i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1)
            parts.Add(char.ConvertToUtf32(s, i).ToString("X4", CultureInfo.InvariantCulture));
        return string.Join(' ', parts);
    }
}
