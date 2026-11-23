using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The messages this daemon wrote to one hosted session's terminal, in the order it wrote them, and the rows already
/// placed against them, so a person-authored row can carry the message it came from. Exact or nothing: a row it can't
/// account for suspends it, and only the session's next process ends a suspension. Its file holds hashes, never text,
/// and is rewritten whole after every change, so a crash leaves the last state whole
/// </summary>
internal sealed class InputLog {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    readonly string _path;
    readonly TimeProvider _time;
    readonly Lock _gate = new();
    readonly List<InputLogEntry> _entries = [];
    readonly List<InputLogMatch> _matches = [];

    int _firstLine;
    // A line at or below it is the server's already, so a rewind that delivers it again must not place it against a
    // later entry. Written only beside another change: after a restart the new process's first line covers it.
    int _acknowledgedThrough = -1;
    long _nextPosition;
    string? _suspended;
    DateTimeOffset? _suspendedAt;
    bool _retired;

    /// <summary>Loads what <paramref name="path"/> holds. A file that can't be read loads suspended, and so does one
    /// whose last entry may or may not have reached the terminal</summary>
    public InputLog(string path, TimeProvider time) {
        _path = path;
        _time = time;

        Load();
    }

    public bool IsSuspended {
        get {
            lock (_gate) return _suspended is not null;
        }
    }

    /// <summary>Records <paramref name="text"/> as about to be pasted, on disk before the paste can start</summary>
    /// <returns>The entry's place, for <see cref="MarkWritten"/> or <see cref="Abandon"/></returns>
    public long AppendPending(InputAuthor author, string text) {
        lock (_gate) {
            var position = _nextPosition++;
            _entries.Add(new InputLogEntry(position, author.DispatchId, author.IsOwner, TextHash(text), Written: false, _time.GetUtcNow()));
            Persist();

            return position;
        }
    }

    /// <summary>The paste and its submit went out. A row read first has already retired the entry</summary>
    public void MarkWritten(long position) => Update(position, entry => entry with { Written = true });

    /// <summary>The paste never started, so no row comes for it</summary>
    public void Abandon(long position) => Update(position, null);

    public void Suspend(string reason) {
        lock (_gate) {
            if (SuspendLocked(reason)) Persist();
        }
    }

    /// <summary>Whether a line with no match record would be compared, so a caller can skip reading one that won't</summary>
    public bool Compares(int lineNumber) {
        lock (_gate) return ComparesLocked(lineNumber);
    }

    /// <summary>The stamp for the row <paramref name="rawLine"/> records, or null. A line placed before answers from its
    /// match record and consumes nothing. Otherwise a person-authored row, given by <paramref name="personText"/>, is
    /// compared with the oldest open entry, Pending or Written, since the row can be read before the write is marked: a
    /// match retires the entry, and anything else suspends</summary>
    public LineStamp? Match(int lineNumber, string rawLine, string? personText) {
        lock (_gate) {
            if (_matches.Find(match => match.Line == lineNumber) is { } placed) {
                if (placed.LineHash == Hash(rawLine)) return Stamp(lineNumber, placed.DispatchId, placed.Local);

                return SuspendWithNoStamp("a line already placed came back changed");
            }

            if (personText is null || !ComparesLocked(lineNumber)) return null;
            if (_entries.Count == 0) return SuspendWithNoStamp("a person-authored row has no message to match");

            var next = _entries[0];
            if (next.TextHash != TextHash(personText)) return SuspendWithNoStamp("a person-authored row is not the next message");

            _entries.RemoveAt(0);
            _matches.Add(new InputLogMatch(lineNumber, Hash(rawLine), next.DispatchId, next.Local));
            Persist();

            return Stamp(lineNumber, next.DispatchId, next.Local);
        }
    }

    /// <summary>The server or the spool holds every line through <paramref name="throughLine"/>, so their match records
    /// go</summary>
    public void Acknowledge(int throughLine) {
        lock (_gate) {
            if (throughLine <= _acknowledgedThrough) return;

            _acknowledgedThrough = throughLine;
            if (_matches.RemoveAll(match => match.Line <= throughLine) > 0) Persist();
        }
    }

    /// <summary>A new process is about to replace the session's last one, which can add no row now. Its open entries
    /// go, and stamping starts again from <paramref name="firstLine"/>, the first line the old process didn't write</summary>
    public void ResetForNewProcess(int firstLine) {
        lock (_gate) {
            _entries.Clear();
            _suspended   = null;
            _suspendedAt = null;
            _firstLine   = firstLine;
            Persist();
        }
    }

    /// <summary>Its file was deleted, so nothing writes it again and nothing is stamped from it</summary>
    internal void Retire() {
        lock (_gate) {
            _retired = true;
            SuspendLocked("its session's recording finished");
        }
    }

    bool ComparesLocked(int lineNumber) => _suspended is null && lineNumber >= _firstLine && lineNumber > _acknowledgedThrough;

    void Update(long position, Func<InputLogEntry, InputLogEntry>? change) {
        lock (_gate) {
            var index = _entries.FindIndex(entry => entry.Position == position);
            if (index < 0) return;

            if (change is null) _entries.RemoveAt(index);
            else _entries[index] = change(_entries[index]);

            Persist();
        }
    }

    LineStamp? SuspendWithNoStamp(string reason) {
        if (SuspendLocked(reason)) Persist();

        return null;
    }

    bool SuspendLocked(string reason) {
        if (_suspended is not null) return false;

        _suspended   = reason;
        _suspendedAt = _time.GetUtcNow();

        return true;
    }

    static LineStamp? Stamp(int lineNumber, Guid? dispatchId, bool local) =>
        dispatchId is null && !local ? null : new LineStamp(lineNumber, dispatchId, local);

    static string TextHash(string text) => Hash(text.TrimEnd('\r', '\n'));

    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    void Load() {
        InputLogState? state;

        try {
            if (!File.Exists(_path)) return;

            state = JsonSerializer.Deserialize(File.ReadAllText(_path), InputLogJsonContext.Default.InputLogState);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            state = null;
        }

        if (state is not { Entries: { } entries, Matches: { } matches }
         || entries.Any(entry => entry?.TextHash is null) || matches.Any(match => match?.LineHash is null)) {
            SuspendLocked("its file could not be read");

            return;
        }

        _firstLine           = state.FirstLine;
        _acknowledgedThrough = state.AcknowledgedThrough;
        _nextPosition        = state.NextPosition;
        _suspended           = state.Suspended;
        _suspendedAt         = state.SuspendedAt;
        _entries.AddRange(entries);
        _matches.AddRange(matches);

        if (_entries.Any(entry => !entry.Written)) SuspendLocked("a message may or may not have reached the terminal before a restart");
    }

    // A write the disk refuses leaves the file behind what is held here, and a crash would then recover the older
    // state, so nothing more is stamped.
    void Persist() {
        if (_retired) return;

        var state = new InputLogState(_firstLine, _acknowledgedThrough, _nextPosition, _suspended, _suspendedAt, [.. _entries], [.. _matches]);

        try {
            var directory = Path.GetDirectoryName(_path)!;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, OwnerOnly | UnixFileMode.UserExecute);

            AtomicFile.Replace(_path, JsonSerializer.Serialize(state, InputLogJsonContext.Default.InputLogState), OwnerOnly, followLink: false, durable: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            SuspendLocked("its file could not be written");
        }
    }
}
