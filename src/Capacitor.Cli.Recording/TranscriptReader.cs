using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording;

/// <summary>
/// Reads a transcript the way a recorder tails it: only complete lines past a line cursor, sharing
/// the file so the agent can keep writing it.
/// </summary>
public static class TranscriptReader {

    /// <summary>
    /// Whether a JSONL transcript's final line is complete: the file is empty, ends in a newline, or its
    /// newline-less last line parses as JSON. A length that stopped changing proves nothing, because a large
    /// write can pause mid-record for longer than any bounded wait.
    /// </summary>
    internal static bool IsFinalLineComplete(string fileText) {
        if (fileText.Length == 0 || fileText[^1] == '\n') return true;

        var lastNl = fileText.LastIndexOf('\n');
        var tail   = lastNl < 0 ? fileText : fileText[(lastNl + 1)..];

        // A whitespace-only trailing partial carries nothing to lose → treat as complete; otherwise
        // the tail must parse as a complete JSON record (length-stability alone is NOT proof).
        return tail.Trim().Length == 0 || IsCompleteJsonRecord(tail);
    }

    static readonly UTF8Encoding Utf8NoPreamble = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The file's text from its last <c>'\n'</c> on, or all of it when it has none: what
    /// <see cref="IsFinalLineComplete"/> decides on, read without decoding the lines before it. A UTF-16 or
    /// UTF-32 file is read whole, since its newline is not a lone <c>0x0A</c> byte.
    /// </summary>
    internal static async Task<string> ReadFinalLineSharedAsync(string path) {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        return await ReadFinalLineAsync(stream, path);
    }

    internal static async Task<string> ReadFinalLineAsync(FileStream stream, string path) {
        if (stream.Length > 0 && stream.ReadByte() is 0xFE or 0xFF or 0x00) return await File.ReadAllTextSharedAsync(path);

        var end   = stream.Length;
        var start = end;

        while (start > 0) {
            var chunk = new byte[(int)Math.Min(64 * 1024, start)];
            stream.Position = start - chunk.Length;
            if (await ReadFullyAsync(stream, chunk, CancellationToken.None) != chunk.Length) break;

            var newline = chunk.AsSpan().LastIndexOf((byte)'\n');
            start -= chunk.Length;
            if (newline >= 0) {
                start += newline;
                break;
            }
        }

        var tail = new byte[end - start];
        stream.Position = start;
        var text = tail.AsSpan(0, await ReadFullyAsync(stream, tail, CancellationToken.None));

        return Utf8NoPreamble.GetString(start == 0 && text.StartsWith(Encoding.UTF8.Preamble) ? text[3..] : text);
    }

    /// <summary>
    /// Waits up to 2s for <paramref name="path"/>'s final line to complete before a final drain. Never
    /// throws: a failed read counts as not yet complete. Shares read and write, because this polls while the
    /// agent flushes its last records (see <see cref="SharedFileText"/>).
    /// </summary>
    internal static async Task<bool> WaitForFinalLineCompletionAsync(
            string path, TimeProvider time, int attempts = 4, int delayMs = 500) {
        for (var i = 0; i < attempts; i++) {
            try {
                if (IsFinalLineComplete(await ReadFinalLineSharedAsync(path))) return true;
            } catch {
                // Transient read failure (e.g. concurrent write) — try again on the next iteration.
            }

            try {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), time);
            } catch {
                // Never let a delay hiccup abort the shutdown path.
            }
        }

        return false;
    }

    /// <summary>
    /// The new complete lines of <paramref name="fileText"/> beyond <paramref name="linesProcessed"/>. A final
    /// line with no newline is the agent mid-write, so it is held back from the lines and from
    /// <see cref="NewTranscriptLines.NextPosition"/> for a later drain to read whole. Blank lines are skipped
    /// but still advance the position.
    /// </summary>
    internal static NewTranscriptLines SplitNewCompleteLines(
            string                    fileText,
            int                       linesProcessed,
            IncompleteFinalLinePolicy policy = IncompleteFinalLinePolicy.Hold
        ) {
        var newLines       = new List<string>();
        var newLineNumbers = new List<int>();

        var endsWithNewline = fileText.Length == 0 || fileText[^1] == '\n';

        using var reader    = new StringReader(fileText);
        var       lineIndex = 0;

        while (reader.ReadLine() is { } line) {
            if (lineIndex >= linesProcessed && !string.IsNullOrWhiteSpace(line)) {
                newLines.Add(line);
                newLineNumbers.Add(lineIndex);
            }

            lineIndex++;
        }

        return ApplyPartialLineHoldback(
            newLines, newLineNumbers, nextPosition: lineIndex, linesProcessed, endsWithNewline, policy);
    }

    /// <summary>
    /// Reads until <paramref name="buffer"/> is full or the file ends, since one read may fill only part of
    /// it, and returns the bytes read.
    /// </summary>
    internal static async Task<int> ReadFullyAsync(FileStream stream, byte[] buffer, CancellationToken ct) {
        var total = 0;

        while (total < buffer.Length) {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n == 0) break;
            total += n;
        }

        return total;
    }

    /// <summary>
    /// The byte offset just past the newlines of the first <paramref name="ackedLineCount"/> lines of
    /// <paramref name="range"/>, which starts at <paramref name="rangeStartOffset"/>. The server can acknowledge
    /// fewer lines than it was sent, so a checkpoint must not assume the whole range was delivered.
    /// </summary>
    internal static long ByteOffsetForAckedLines(ReadOnlySpan<byte> range, long rangeStartOffset, int ackedLineCount) {
        if (ackedLineCount <= 0) return rangeStartOffset;

        var seen = 0;

        for (var i = 0; i < range.Length; i++) {
            if (range[i] != (byte)'\n') continue;

            seen++;

            if (seen == ackedLineCount) return rangeStartOffset + i + 1;
        }

        // Acked at least as many lines as this range's newlines account for (a full ack) — the
        // whole verified range was consumed.
        return rangeStartOffset + range.Length;
    }

    /// <summary>
    /// The byte offset just past line <paramref name="lineNumber"/>'s newline, with the line count that
    /// offset stands for. Null when the file is missing or has fewer lines: a cursor clamped to the end of
    /// a truncated file would seed the rewrite guard against the wrong file, so the caller quarantines.
    /// </summary>
    /// <remarks>
    /// A complete record with no newline yet, at the line after the last newline, resolves to the record's
    /// own start and one line fewer, not to the end of the file. Its newline lands later, and a cursor at
    /// the end would read that newline as an empty line and number every later line one too high. The
    /// record is read and sent again, which the server dedupes by its event ids.
    /// </remarks>
    internal static async Task<(long ByteOffset, int LineNumber)?> ResolveByteOffsetForLineAsync(string transcriptPath, int lineNumber, CancellationToken ct) {
        if (lineNumber <= 0) return (0, lineNumber);
        if (!File.Exists(transcriptPath)) return null;

        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[81920];
        var seen   = 0;
        long offset = 0;
        long lastNewlineEnd = 0;

        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) {
            for (var i = 0; i < read; i++) {
                offset++;

                if (buffer[i] != (byte)'\n') continue;

                seen++;
                if (seen == lineNumber) return (offset, lineNumber);
                lastNewlineEnd = offset;
            }
        }

        // Fewer newline-terminated lines than requested. The one remaining possibility is that the
        // requested line IS the final, unterminated-but-complete record — never a shortfall further
        // back than that.
        if (lineNumber != seen + 1 || offset <= lastNewlineEnd) return null;

        var trailingLength = (int)(offset - lastNewlineEnd);
        var trailing        = new byte[trailingLength];
        stream.Position      = lastNewlineEnd;
        var trailingRead     = await ReadFullyAsync(stream, trailing, ct);

        // A short read means the file shrank since the scan, and the rest of the buffer is zeroes rather than
        // the record, so nothing below may judge it.
        if (trailingRead != trailingLength) return null;

        return IsCompleteJsonRecord(Encoding.UTF8.GetString(trailing))
            ? (lastNewlineEnd, lineNumber - 1)
            : null;
    }

    /// <summary>
    /// Streams the new complete lines beyond <paramref name="linesProcessed"/>, keeping only those. The
    /// file length is sampled once and the read capped there, so an append after the sample cannot make an
    /// unterminated final line look complete. The caller opens <paramref name="stream"/> with
    /// <see cref="FileShare.ReadWrite"/>, so the agent is never blocked.
    /// </summary>
    /// <param name="captureRawBytes">Decode from a captured buffer, so the rewrite guard hashes the exact
    /// bytes the lines came from rather than a later read of a file that may have changed.</param>
    /// <param name="rawBytesReadFrom">With <paramref name="captureRawBytes"/>, the offset the buffer starts
    /// at. A caller that needs only a window passes it, so the buffer is sized to what is new.</param>
    /// <param name="newRangeByteOffset">With <paramref name="captureRawBytes"/>, the offset of line
    /// <paramref name="linesProcessed"/>, which must be a line boundary. Bytes before it are never decoded.
    /// Null decodes the whole buffer and filters by line index.</param>
    /// <param name="lineOffsets">Ignored with <paramref name="captureRawBytes"/>. Otherwise starts the read at
    /// the cursor's byte offset instead of byte 0, and learns the offsets of the lines it reads. The
    /// result is the same as without it.</param>
    public static async Task<NewTranscriptLines> ReadNewCompleteLinesAsync(
            FileStream                stream,
            int                       linesProcessed,
            IncompleteFinalLinePolicy policy,
            CancellationToken         ct,
            bool                      captureRawBytes    = false,
            long                      rawBytesReadFrom   = 0,
            long?                     newRangeByteOffset = null,
            TranscriptLineIndex?      lineOffsets        = null) {
        var length = stream.Length;

        bool endsWithNewline;
        if (length == 0) {
            endsWithNewline = true;
        } else {
            stream.Seek(length - 1, SeekOrigin.Begin);
            endsWithNewline = stream.ReadByte() == '\n';
            stream.Seek(0, SeekOrigin.Begin);
        }

        var newLines           = new List<string>();
        var newLineNumbers     = new List<int>();
        var lineIndex          = 0;
        byte[]? rawBytes       = null;
        var     snapshotStart  = 0L;


        // A capture decodes from the buffer rather than the stream, so the guard's hash and the lines come
        // from one read.
        if (captureRawBytes) {
            // A window, not the whole file: an idle poll allocates the guard's tail zone, not file length.
            snapshotStart = Math.Min(rawBytesReadFrom, length);
            if (snapshotStart > 0) stream.Position = snapshotStart;

            var buffer = new byte[length - snapshotStart];
            var total  = 0;

            while (total < buffer.Length) {
                var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (n == 0) break;
                total += n;
            }

            // A shrink after the length sample leaves total short, and a snapshot must not claim bytes
            // that were never read.
            rawBytes = total == buffer.Length ? buffer : buffer[..total];
            length   = snapshotStart + rawBytes.Length;

            if (newRangeByteOffset is { } nro) {
                // Every byte at/after this position in the buffer is NEW (the offset is guaranteed
                // to land on a line boundary — see the parameter doc); bytes before it are the
                // prior-tail zone, captured only so the guard can hash it, never decoded as lines.
                var decodeFrom = (int)Math.Clamp(nro - snapshotStart, 0, rawBytes.Length);
                lineIndex = linesProcessed;

                using var bytesReader = new StreamReader(
                    new MemoryStream(rawBytes, decodeFrom, rawBytes.Length - decodeFrom, writable: false));
                while (await bytesReader.ReadLineAsync(ct) is { } line) {
                    if (!string.IsNullOrWhiteSpace(line)) {
                        newLines.Add(line);
                        newLineNumbers.Add(lineIndex);
                    }

                    lineIndex++;
                }
            } else {
                // No known offset for the new range, so the buffer is the whole file.
                using var bytesReader = new StreamReader(new MemoryStream(rawBytes, writable: false));
                while (await bytesReader.ReadLineAsync(ct) is { } line) {
                    if (lineIndex >= linesProcessed && !string.IsNullOrWhiteSpace(line)) {
                        newLines.Add(line);
                        newLineNumbers.Add(lineIndex);
                    }

                    lineIndex++;
                }
            }
        } else {
            var startOffset = 0L;
            if (lineOffsets is not null) {
                (lineIndex, startOffset) = await lineOffsets.StartAsync(stream, length, linesProcessed, ct);
                stream.Position          = startOffset;
            }

            // Mid-file there is no byte-order mark to detect, and a line that happens to begin with
            // the UTF-8 one must keep it, as it does when decoded from byte 0.
            using var reader = startOffset == 0
                ? new StreamReader(new LengthLimitedReadStream(stream, length, lineOffsets), leaveOpen: true)
                : new StreamReader(new LengthLimitedReadStream(stream, length - startOffset, lineOffsets),
                    Utf8NoPreamble, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            while (await reader.ReadLineAsync(ct) is { } line) {
                if (lineIndex >= linesProcessed && !string.IsNullOrWhiteSpace(line)) {
                    newLines.Add(line);
                    newLineNumbers.Add(lineIndex);
                }

                lineIndex++;
            }

            if (lineOffsets is not null) await lineOffsets.FinishAsync(stream, ct);
        }

        // The boundary this read was capped at: a later length sample could count bytes it never saw.
        return ApplyPartialLineHoldback(
            newLines, newLineNumbers, nextPosition: lineIndex, linesProcessed, endsWithNewline, policy,
            snapshotByteLength: length, snapshotBytes: rawBytes, snapshotStartOffset: snapshotStart);
    }

    // Under Hold an unterminated final line is held back for a later drain. Under ConsumeIfComplete it
    // is consumed only if the bytes read parse as a complete JSON record, and otherwise reported through
    // HeldIncompleteFinalLine. The parse runs on the bytes consumed, so an earlier completeness probe
    // cannot go stale.

    static NewTranscriptLines ApplyPartialLineHoldback(
            List<string>              newLines,
            List<int>                 newLineNumbers,
            int                       nextPosition,
            int                       linesProcessed,
            bool                      endsWithNewline,
            IncompleteFinalLinePolicy policy,
            long                      snapshotByteLength  = 0,
            byte[]?                   snapshotBytes       = null,
            long                      snapshotStartOffset = 0) {
        if (endsWithNewline || nextPosition <= linesProcessed) {
            return new NewTranscriptLines(
                newLines, newLineNumbers, nextPosition,
                SnapshotByteLength: snapshotByteLength, SnapshotBytes: snapshotBytes,
                SnapshotStartOffset: snapshotStartOffset);
        }

        var partialIndex = nextPosition - 1;
        var hasPartial   = newLineNumbers.Count > 0 && newLineNumbers[^1] == partialIndex;

        // Consume the unterminated final line only when the policy allows it AND the exact bytes
        // read form a complete JSON record. Anything else (Hold, blank/whitespace partial, or an
        // unparseable tail) is held back.
        var consume = policy == IncompleteFinalLinePolicy.ConsumeIfComplete
                      && hasPartial
                      && IsCompleteJsonRecord(newLines[^1]);

        if (consume) {
            return new NewTranscriptLines(
                newLines, newLineNumbers, nextPosition,
                SnapshotByteLength: snapshotByteLength, SnapshotBytes: snapshotBytes,
                SnapshotStartOffset: snapshotStartOffset);
        }

        if (hasPartial) {
            newLines.RemoveAt(newLines.Count - 1);
            newLineNumbers.RemoveAt(newLineNumbers.Count - 1);
        }

        // The snapshot still covers the held tail: the guard must check those bytes though they stay unsent.
        return new NewTranscriptLines(
            newLines, newLineNumbers, partialIndex, HeldIncompleteFinalLine: hasPartial,
            SnapshotByteLength: snapshotByteLength, SnapshotBytes: snapshotBytes,
            SnapshotStartOffset: snapshotStartOffset);
    }

    /// <summary>True if <paramref name="line"/>, a single transcript line with no newline, parses as a
    /// complete JSON document.</summary>
    static bool IsCompleteJsonRecord(string line) => JsonRecord.IsComplete(line);
}
