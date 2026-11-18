using System.Text;

namespace Capacitor.Cli.Core;

/// <summary>A text file's content, decoded the way <c>File.ReadAllText</c> decodes it: UTF-8 unless a byte order mark
/// names UTF-16 or UTF-32, as PowerShell 5.1's <c>&gt;</c> and <c>Out-File</c> write. A rewrite is UTF-8, with a BOM
/// only when the file had a UTF-8 one; decoding the raw bytes as UTF-8 instead turns a UTF-16 file into U+FFFD and
/// NUL-interleaved text.</summary>
public readonly record struct TextFileContent(string Text, bool Utf8Bom) {
    static readonly UTF8Encoding WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    static readonly UTF8Encoding WithBom    = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>The encoding a rewrite of this content uses.</summary>
    public UTF8Encoding Encoding => Utf8Bom ? WithBom : WithoutBom;

    public static TextFileContent Read(string path) {
        using var reader = new StreamReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            WithoutBom,
            detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();

        // Detection swaps in an encoding carrying the preamble it found; with none found it stays WithoutBom.
        var bom = reader.CurrentEncoding is UTF8Encoding && reader.CurrentEncoding.Preamble.Length > 0;

        return new(text, bom);
    }
}
