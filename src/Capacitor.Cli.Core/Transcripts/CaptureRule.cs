using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>The limits the recorder's secret redaction reads a transcript line under, and what follows from them for a
/// line before any secret is looked for. A replay that projects raw lines skips the ones a capture replaced with a
/// capture-loss line, which nothing projects, so it rebuilds what the batches after them were built on.</summary>
public static class CaptureRule {
    /// <summary>The most UTF-8 bytes a captured line has, as read and as rewritten.</summary>
    public const int MaxRecordBytes = 4 * 1024 * 1024;

    /// <summary>A container opened this deep loses its line.</summary>
    public const int MaxDepth = 1000;

    /// <summary>The most a rewrite grows a line: a one-byte character the writer escapes, such as DEL, becomes six.</summary>
    public const int MaxRewriteGrowth = 6;

    // One level past the limit, so going too deep reads as depth rather than as malformed text.
    public static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = MaxDepth + 1, CommentHandling = JsonCommentHandling.Allow };

    /// <summary>A line too large, or one the redaction cannot read, is lost; one a rewrite could take past
    /// <see cref="MaxRecordBytes"/> is undecided, since that depends on the secrets in it.</summary>
    public static LineCapture Of(string rawLine) {
        var bytes = Encoding.UTF8.GetByteCount(rawLine);
        if (bytes > MaxRecordBytes) return LineCapture.Lost;
        if (!Readable(rawLine, bytes)) return LineCapture.Lost;

        return bytes > MaxRecordBytes / MaxRewriteGrowth ? LineCapture.Undecided : LineCapture.Kept;
    }

    // What the redaction's reader stops on: malformed JSON, a container at MaxDepth, a string that is not valid UTF-16.
    static bool Readable(string line, int byteCount) {
        if (line.Length == 0) return true;

        var input   = ArrayPool<byte>.Shared.Rent(byteCount);
        var decoded = ArrayPool<char>.Shared.Rent(line.Length);

        try {
            Encoding.UTF8.GetBytes(line, input.AsSpan());
            var reader = new Utf8JsonReader(input.AsSpan(0, byteCount), ReaderOptions);

            while (reader.Read()) {
                if (reader.CurrentDepth >= MaxDepth && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) return false;
                if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String) reader.CopyString(decoded);
            }

            return true;
        } catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) {
            return false;
        } finally {
            ArrayPool<char>.Shared.Return(decoded, clearArray: true);
            ArrayPool<byte>.Shared.Return(input, clearArray: true);
        }
    }
}
