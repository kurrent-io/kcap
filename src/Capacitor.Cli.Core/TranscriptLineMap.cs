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

    // The lines a rewrite renumbered that had already been sent, each with the number it went out under, waiting for
    // the read that finds what the rewrite kept of them. Only lines below the end of what had been sent can be a copy.
    List<(ulong Hash, int Sent)> _candidates = [];
    int                          _candidatesEnd;

    // From each segment's first file line on, a line is sent under its file number plus the segment's shift.
    readonly List<(int FileLine, int Shift)> _segments = [(0, 0)];

    // File lines a rewrite kept unchanged from lines already sent, each with the number its original went out under.
    readonly Dictionary<int, int> _kept = [];

    /// <summary>How many lines from the top of the file have been read.</summary>
    public int Known => _hashes.Count;

    /// <summary>Counts a poll; true for the first and every <paramref name="every"/>th after it.</summary>
    public bool PollIsDue(int every) => _polls++ % every == 0;

    public TranscriptLineMap() { }

    /// <summary>A map numbering lines as <paramref name="segments"/> say, with the copies <paramref name="kept"/>
    /// names, and holding no hashes.</summary>
    public TranscriptLineMap(IEnumerable<(int FileLine, int Shift)> segments, IEnumerable<KeyValuePair<int, int>> kept) {
        foreach (var segment in segments) {
            if (segment.FileLine > _segments[^1].FileLine) _segments.Add(segment);
            else if (segment.FileLine == 0) _segments[0] = segment;
        }

        foreach (var (fileLine, sent) in kept) _kept[fileLine] = sent;
    }

    /// <summary>File lines a rewrite kept unchanged from lines already sent, each with its original's sent number.</summary>
    public IReadOnlyDictionary<int, int> Kept => _kept;

    /// <summary>
    /// Whether <paramref name="fileLine"/> is a copy a rewrite kept of a line the server holds: one sent below
    /// <paramref name="sentCursor"/>. A server that lost the original needs the copy.
    /// </summary>
    public bool HeldBack(int fileLine, int sentCursor) => _kept.TryGetValue(fileLine, out var sent) && sent < sentCursor;

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
    /// been sent, and a copy of one the rewrite kept is found by <see cref="Match"/>.
    /// </summary>
    public void Rewritten(int fileLine, int nextSentLine, int fileCursor) {
        // An earlier rewrite the cursor has not got past yet still has lines waiting for their copy: they carry over.
        var carried = _candidates.Count > 0 && fileCursor < _candidatesEnd ? _candidates : [];

        _candidatesEnd = carried.Count > 0 ? Math.Max(_candidatesEnd, fileCursor) : fileCursor;
        _candidates    = [..carried];
        for (var i = fileLine; i < Math.Min(fileCursor, _hashes.Count); i++) {
            if (_hashes[i] != BlankHash) _candidates.Add((_hashes[i], _kept.TryGetValue(i, out var sent) ? sent : ToSent(i)));
        }

        foreach (var line in _kept.Keys.Where(k => k >= fileLine).ToList()) _kept.Remove(line);

        if (fileLine < _hashes.Count) _hashes.RemoveRange(fileLine, _hashes.Count - fileLine);
        _segments.RemoveAll(s => s.FileLine >= fileLine && s.FileLine > 0);

        var shift = nextSentLine - fileLine;
        if (shift != SegmentAt(fileLine).Shift) _segments.Add((fileLine, shift));
    }

    /// <summary>
    /// Finds which of <paramref name="lines"/>, read from <paramref name="fileCursor"/> on, are copies a rewrite kept of
    /// lines already sent, each sent line accounting for one copy. A line is judged once, so a read whose send fails is
    /// judged the same way again. True when it found one.
    /// </summary>
    public bool Match(IReadOnlyList<string> lines, IReadOnlyList<int> fileLines, int fileCursor) {
        var found = false;

        for (var i = 0; i < lines.Count && _candidates.Count > 0; i++) {
            var at = fileLines[i];
            if (at < fileCursor || _kept.ContainsKey(at)) continue;

            if (at >= _candidatesEnd) {
                _candidates.Clear();
                break;
            }

            var hash  = Hash(lines[i]);
            var match = _candidates.FindIndex(c => c.Hash == hash);
            if (match < 0) continue;

            _kept[at] = _candidates[match].Sent;
            _candidates.RemoveAt(match);
            found = true;
        }

        return found;
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
