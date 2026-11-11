using System.Buffers;
using System.Globalization;
using System.Text;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Reads one HTTP/1.1 request off the steering socket. A header block past its cap, or a body declared past
/// its cap, is refused before any more of it is read. Bodies are framed by <c>Content-Length</c> only.</summary>
internal static class SteeringHttpReader {
    static readonly SearchValues<char> TokenChars =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>The request, or null when the client closed before sending all of it. A request to refuse throws
    /// <see cref="SteeringHttpRefusal"/>.</summary>
    public static async Task<SteeringHttpRequest?> ReadAsync(Stream stream, int maxHeaderBytes, int maxBodyBytes, CancellationToken ct) {
        var buffer = new byte[maxHeaderBytes];
        var filled = 0;
        int headLength;

        while (true) {
            if (filled == buffer.Length) throw new SteeringHttpRefusal(431, "headers_too_large");

            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0) return null;

            // The terminator can straddle two reads.
            var from = Math.Max(0, filled - 3);
            filled += read;

            var at = buffer.AsSpan(from, filled - from).IndexOf("\r\n\r\n"u8);
            if (at < 0) continue;

            headLength = from + at;

            break;
        }

        var (method, path, contentLength) = ParseHead(buffer.AsSpan(0, headLength), maxBodyBytes);

        var body      = new byte[contentLength];
        var bodyStart = headLength + 4;
        var got       = Math.Min(filled - bodyStart, contentLength);
        buffer.AsSpan(bodyStart, got).CopyTo(body);

        while (got < contentLength) {
            var read = await stream.ReadAsync(body.AsMemory(got), ct);
            if (read == 0) return null;

            got += read;
        }

        return new SteeringHttpRequest(method, path, body);
    }

    static (string Method, string Path, int ContentLength) ParseHead(ReadOnlySpan<byte> head, int maxBodyBytes) {
        // One char per byte, so nothing the checks below refuse can hide inside a multi-byte sequence.
        var lines = Encoding.Latin1.GetString(head).Split("\r\n");

        if (lines[0].Split(' ') is not [var method, var target, "HTTP/1.1" or "HTTP/1.0"]
         || !IsToken(method)
         || !target.StartsWith('/')
         || target.AsSpan().ContainsAnyExceptInRange('!', '~'))
            throw Malformed();

        int? contentLength = null;

        foreach (var line in lines.AsSpan(1)) {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !IsToken(line.AsSpan(0, colon))) throw Malformed();

            var name  = line.AsSpan(0, colon);
            var value = line.AsSpan(colon + 1).Trim(" \t");
            if (value.ContainsAnyInRange('\0', '\b') || value.ContainsAnyInRange('\n', '\x1f') || value.Contains('\x7f'))
                throw Malformed();

            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                throw new SteeringHttpRefusal(501, "transfer_encoding_unsupported");

            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;

            if (contentLength is not null || value.IsEmpty || value.ContainsAnyExceptInRange('0', '9')) throw Malformed();

            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > maxBodyBytes)
                throw new SteeringHttpRefusal(413, "body_too_large");

            contentLength = (int)length;
        }

        var query = target.IndexOf('?');

        return (method, query < 0 ? target : target[..query], contentLength ?? 0);
    }

    static bool IsToken(ReadOnlySpan<char> text) => !text.IsEmpty && !text.ContainsAnyExcept(TokenChars);

    static SteeringHttpRefusal Malformed() => new(400, "malformed_request");
}
