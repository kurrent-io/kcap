using System.Text.Json;
using Capacitor.Cli.Core.Skills;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The sources this daemon records, in its owner-only state directory, so a daemon killed outright can pick
/// each one up from the server's position. A write replaces the whole file, so a crash leaves the last list whole, and
/// the file is read ahead of the first write, whichever comes first, because a local spawn can beat the boot's restore.</summary>
internal sealed partial class RecordedSourceList(string stateDirectory, ILogger logger) {
    const string FileName = "recording-sources.json";

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    readonly string _path = Path.Combine(stateDirectory, FileName);
    readonly Lock _gate = new();
    readonly Dictionary<string, RecordedSource> _sources = new(StringComparer.Ordinal);

    bool _read;

    // A refused write leaves the file behind what is held here. Rolling the removal back instead would have every later
    // write put the finished source back on disk.
    bool _behind;

    /// <summary>What the last daemon left, and whatever this one added since. A list that can't be read is logged and
    /// read as empty: its claims go stale with nothing to pick them up, which is no worse than never having had it.</summary>
    public IReadOnlyList<RecordedSource> Load() {
        lock (_gate) {
            Read();

            return [.. _sources.Values];
        }
    }

    /// <summary>Whether <paramref name="source"/> is on disk now. Nothing would pick up a source that isn't, so a
    /// caller holding a claim for one lets it go.</summary>
    public bool TryAdd(RecordedSource source) {
        lock (_gate) {
            Read();
            _sources[source.Key] = source;

            if (Write()) return true;

            _sources.Remove(source.Key);

            return false;
        }
    }

    public void Remove(string key) {
        lock (_gate) {
            Read();
            if (_sources.Remove(key) || _behind) Write();
        }
    }

    void Read() {
        if (_read) return;

        _read = true;

        try {
            if (!File.Exists(_path)) return;

            foreach (var source in JsonSerializer.Deserialize(File.ReadAllText(_path), RecordedSourceJsonContext.Default.ListRecordedSource) ?? [])
                if (IsWhole(source)) _sources[source.Key] = source;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            LogUnreadable(ex, _path);
        }
    }

    static bool IsWhole(RecordedSource? source) => source is { AgentId: not null, SessionId: not null, TranscriptPath: not null, Vendor: not null };

    bool Write() {
        try {
            Directory.CreateDirectory(stateDirectory);
            AtomicFile.Replace(
                _path, JsonSerializer.Serialize([.. _sources.Values], RecordedSourceJsonContext.Default.ListRecordedSource), OwnerOnly, followLink: false, durable: true);
            _behind = false;

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogUnwritable(ex, _path);
            _behind = true;

            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The recorded sources in {Path} could not be read; a hard-killed daemon's sources stay unfinished")]
    partial void LogUnreadable(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The recorded sources in {Path} could not be written")]
    partial void LogUnwritable(Exception ex, string path);
}
