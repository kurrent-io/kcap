using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core;

public enum TailStatus { Ok, Reset, Missing, Failed }

public sealed record TailRead(IReadOnlyList<string> Lines, TailStatus Status, string? Failure = null) {
    /// Byte positions at the start of each returned line, for correlating newly appended input.
    public IReadOnlyList<long> LineStartOffsets { get; init; } = [];
    /// Length observed when this read began, including a partial final line.
    public long? SnapshotLength { get; init; }
    public string? SourceStamp { get; init; }
}

/// Appended-lines reader over a JSONL file another process is writing. Every open shares
/// read/write/delete so Windows lets the writer keep its handle.
public sealed class JsonlTail(string path) {

    public string Path { get; } = path;
    public long Cursor { get; private set; }
    const int ContinuitySampleSize = 128;
    byte[]? _head;
    byte[]? _boundary;
    DateTime? _created;
    long _sampledCursor;
    string? _sourceStamp;

    public TailRead ReadAppended() => ReadAppended(verifyContinuity: false);

    public TailRead ReadAppended(bool verifyContinuity) {
        try {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var created = verifyContinuity && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                ? File.GetCreationTimeUtc(stream.SafeFileHandle) : (DateTime?)null;
            var regressed = length < Cursor || (verifyContinuity && _head is not null
                && (_created != created || !Matches(stream, 0, _head) || !Matches(stream, _sampledCursor - _boundary!.Length, _boundary)));

            // The origin stays local until the read succeeds: assigning the reset cursor up front
            // would let a fault mid-read return Failed with the regression already spent, and the
            // next Ok read would then deliver the whole file on top of what it had reported.
            var origin = regressed ? 0 : Cursor;
            var status = regressed ? TailStatus.Reset : TailStatus.Ok;
            if (length == origin) {
                if (regressed) ClearContinuity();
                if (verifyContinuity && origin > 0 && _sampledCursor != origin) CaptureContinuity(stream, origin, created, reset: false);
                Cursor = origin;
                return new TailRead([], status) { SnapshotLength = length, SourceStamp = verifyContinuity ? _sourceStamp : null };
            }

            stream.Position = origin;
            var buffer = new byte[length - origin];
            var read = 0;
            while (read < buffer.Length) {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }

            var offsets = new List<long>();
            var lines = SplitCompleteLines(buffer.AsSpan(0, read), out var consumed, offsets, origin);
            var cursor = origin + consumed;
            if (verifyContinuity && cursor > 0 && (regressed || _sampledCursor != cursor))
                CaptureContinuity(stream, cursor, created, regressed);
            else if (regressed) ClearContinuity();
            Cursor = cursor;
            return new TailRead(lines, status) {
                LineStartOffsets = offsets, SnapshotLength = length, SourceStamp = verifyContinuity ? _sourceStamp : null,
            };
        } catch (FileNotFoundException) {
            return new TailRead([], TailStatus.Missing);
        } catch (DirectoryNotFoundException) {
            return new TailRead([], TailStatus.Missing);
        } catch (Exception ex) {
            return new TailRead([], TailStatus.Failed, ex.Message);
        }
    }

    void ClearContinuity() {
        _head = _boundary = null;
        _sampledCursor = 0;
        _sourceStamp = null;
    }

    void CaptureContinuity(FileStream stream, long cursor, DateTime? created, bool reset) {
        var size = (int)Math.Min(cursor, ContinuitySampleSize);
        var boundary = Sample(stream, cursor - size, size);
        if (!reset && _head is not null) {
            (_boundary, _sampledCursor) = (boundary, cursor);
            return;
        }

        // The first complete line's prefix stays the same while the writer appends.
        var head = Sample(stream, 0, size);
        var newline = Array.IndexOf(head, (byte)'\n');
        var first = newline < 0 ? head.AsSpan() : head.AsSpan(0, newline + 1);
        Span<byte> stamp = stackalloc byte[ContinuitySampleSize + sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(stamp, created?.Ticks ?? 0);
        first.CopyTo(stamp[sizeof(long)..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(stamp[..(sizeof(long) + first.Length)], hash);
        (_head, _boundary, _created, _sampledCursor, _sourceStamp) = (head, boundary, created, cursor, Convert.ToHexString(hash[..16]));
    }

    static bool Matches(FileStream stream, long offset, byte[] expected) {
        Span<byte> buffer = stackalloc byte[ContinuitySampleSize];
        stream.Position = offset;
        var actual = buffer[..expected.Length];
        return stream.ReadAtLeast(actual, actual.Length, throwOnEndOfStream: false) == expected.Length
            && actual.SequenceEqual(expected);
    }

    static byte[] Sample(FileStream stream, long offset, int size) {
        var sample = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(sample);
        return sample;
    }

    /// Complete lines only; `consumed` stops after the last '\n' so an unterminated tail is
    /// re-read whole once its newline lands.
    public static List<string> SplitCompleteLines(ReadOnlySpan<byte> bytes, out int consumed) =>
        SplitCompleteLines(bytes, out consumed, null, 0);

    static List<string> SplitCompleteLines(ReadOnlySpan<byte> bytes, out int consumed, List<long>? offsets, long origin) {
        var lines = new List<string>();
        consumed = 0;
        var start = 0;
        for (var i = 0; i < bytes.Length; i++) {
            if (bytes[i] != (byte)'\n') continue;
            var line = bytes[start..i];
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (!IsBlank(line)) {
                lines.Add(Encoding.UTF8.GetString(line));
                offsets?.Add(origin + start);
            }
            start = i + 1;
            consumed = start;
        }
        return lines;
    }

    static bool IsBlank(ReadOnlySpan<byte> line) {
        foreach (var b in line) {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return false;
        }
        return true;
    }
}
