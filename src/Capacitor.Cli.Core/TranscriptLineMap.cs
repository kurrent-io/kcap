using System.IO.Hashing;
using System.Text;

namespace Capacitor.Cli.Core;

/// <summary>
/// For a transcript its agent rewrites in place, the number each file line is sent under, and a hash of every line
/// read, so a rewrite can be told from an append and the first line it changed found. The server drops a line
/// numbered at or below one it took, so lines written after a rewrite are numbered on from the cursor; the lines
/// the rewrite dropped stay stored as they were sent. One reader at a time.
/// </summary>
public sealed class TranscriptLineMap {
    static readonly ulong BlankHash = XxHash3.HashToUInt64([]);

    readonly List<ulong> _hashes = [];

    int _polls;

    // How many copies of each line a rewrite renumbered had already been sent, so one it kept is not sent twice.
    // Only lines from where it started to the end of what had been sent can be such a copy.
    Dictionary<ulong, int> _sentBeforeRewrite = [];
    int                    _sentBeforeRewriteStart;
    int                    _sentBeforeRewriteEnd;

    // From each segment's first file line on, a line is sent under its file number plus the segment's shift.
    readonly List<(int FileLine, int Shift)> _segments = [(0, 0)];

    /// <summary>How many lines from the top of the file have been read.</summary>
    public int Known => _hashes.Count;

    /// <summary>Counts a poll; true for the first and every <paramref name="every"/>th after it.</summary>
    public bool PollIsDue(int every) => _polls++ % every == 0;

    public TranscriptLineMap() { }

    /// <summary>A map numbering lines as <paramref name="segments"/> say, and holding no hashes.</summary>
    public TranscriptLineMap(IEnumerable<(int FileLine, int Shift)> segments) {
        foreach (var segment in segments) {
            if (segment.FileLine > _segments[^1].FileLine) _segments.Add(segment);
            else if (segment.FileLine == 0) _segments[0] = segment;
        }
    }

    /// <summary>From each segment's file line on, a line is sent under its file number plus the segment's shift.</summary>
    public IReadOnlyList<(int FileLine, int Shift)> Segments => _segments;

    public int ToSent(int fileLine) => fileLine + SegmentAt(fileLine).Shift;

    /// <summary>The first file line sent under <paramref name="sentLine"/> or later.</summary>
    public int ToFile(int sentLine) {
        for (var i = 0; i < _segments.Count; i++) {
            var (start, shift) = _segments[i];
            var last           = i == _segments.Count - 1;

            if (last || sentLine < _segments[i + 1].FileLine + shift) return Math.Max(start, sentLine - shift);
        }

        return sentLine;
    }

    /// <summary>
    /// Records lines read at their file line numbers. Every other line below <paramref name="nextLine"/>, where the
    /// read ended, is blank, and is recorded as blank: a cursor that moved past it must be able to tell it changed.
    /// </summary>
    public void Observe(IReadOnlyList<string> lines, IReadOnlyList<int> fileLines, int nextLine) {
        for (var i = 0; i < lines.Count; i++) {
            var at = fileLines[i];
            while (_hashes.Count < at) _hashes.Add(BlankHash);

            if (at < _hashes.Count) _hashes[at] = Hash(lines[i]);
            else _hashes.Add(Hash(lines[i]));
        }

        while (_hashes.Count < nextLine) _hashes.Add(BlankHash);
    }

    /// <summary>Whether <paramref name="fileLine"/> still reads <paramref name="line"/>, null meaning blank.</summary>
    public bool Matches(int fileLine, string? line) => fileLine < _hashes.Count && _hashes[fileLine] == Hash(line);

    /// <summary>
    /// The first line below <paramref name="upTo"/> whose content in the file at <paramref name="path"/> differs from
    /// what was read, or where the file now ends; <paramref name="upTo"/> when none does.
    /// </summary>
    public async Task<int> FirstChangedLineAsync(string path, int upTo, CancellationToken ct) {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        var limit = Math.Min(upTo, _hashes.Count);
        var line  = 0;

        while (line < limit && await reader.ReadLineAsync(ct) is { } text) {
            if (_hashes[line] != Hash(text)) return line;
            line++;
        }

        return line;
    }

    /// <summary>
    /// Forgets what was read from <paramref name="fileLine"/> on, which a rewrite changed, and numbers the file's lines
    /// from there on from <paramref name="nextSentLine"/>. The lines from there up to <paramref name="fileCursor"/> had
    /// been sent.
    /// </summary>
    public void Rewritten(int fileLine, int nextSentLine, int fileCursor) {
        // An earlier rewrite the cursor has not got past yet still has sent lines no read has accounted for: they
        // carry over, less the copies the lines read since have taken.
        var carried = _sentBeforeRewrite.Count > 0 && fileCursor < _sentBeforeRewriteEnd
            ? new Dictionary<ulong, int>(_sentBeforeRewrite)
            : [];
        if (carried.Count > 0) {
            for (var i = _sentBeforeRewriteStart; i < Math.Min(fileCursor, _hashes.Count); i++) Take(carried, _hashes[i]);
        }

        _sentBeforeRewriteEnd   = carried.Count > 0 ? _sentBeforeRewriteEnd : fileCursor;
        _sentBeforeRewriteStart = fileLine;
        _sentBeforeRewrite      = carried;
        for (var i = fileLine; i < Math.Min(fileCursor, _hashes.Count); i++) {
            if (_hashes[i] != BlankHash) _sentBeforeRewrite[_hashes[i]] = _sentBeforeRewrite.GetValueOrDefault(_hashes[i]) + 1;
        }

        if (fileLine < _hashes.Count) _hashes.RemoveRange(fileLine, _hashes.Count - fileLine);
        _segments.RemoveAll(s => s.FileLine >= fileLine && s.FileLine > 0);

        var shift = nextSentLine - fileLine;
        if (shift != SegmentAt(fileLine).Shift) _segments.Add((fileLine, shift));
    }

    /// <summary>
    /// Which of <paramref name="lines"/>, read from <paramref name="fileCursor"/> on, are copies the last rewrite kept
    /// of lines already sent. Each sent line accounts for one copy, and the copies the lines below the cursor took are
    /// counted again on every call rather than used up, so a read whose send fails is judged the same way again.
    /// </summary>
    public bool[] KeptCopies(IReadOnlyList<string> lines, IReadOnlyList<int> fileLines, int fileCursor) {
        var kept = new bool[lines.Count];
        if (_sentBeforeRewrite.Count == 0 || fileCursor >= _sentBeforeRewriteEnd) return kept;

        var copies = new Dictionary<ulong, int>(_sentBeforeRewrite);
        for (var i = _sentBeforeRewriteStart; i < Math.Min(fileCursor, _hashes.Count); i++) Take(copies, _hashes[i]);

        for (var i = 0; i < lines.Count; i++) {
            if (fileLines[i] >= fileCursor && fileLines[i] < _sentBeforeRewriteEnd) kept[i] = Take(copies, Hash(lines[i]));
        }

        return kept;
    }

    static bool Take(Dictionary<ulong, int> copies, ulong hash) {
        if (!copies.TryGetValue(hash, out var left)) return false;

        if (left == 1) copies.Remove(hash);
        else copies[hash] = left - 1;

        return true;
    }

    (int FileLine, int Shift) SegmentAt(int fileLine) {
        for (var i = _segments.Count - 1; i > 0; i--) {
            if (_segments[i].FileLine <= fileLine) return _segments[i];
        }

        return _segments[0];
    }

    static ulong Hash(string? line) =>
        string.IsNullOrWhiteSpace(line) ? BlankHash : XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(line));
}
