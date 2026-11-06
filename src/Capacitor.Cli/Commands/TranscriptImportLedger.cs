using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Per-server record of how far each transcript was imported. The server's last-line
/// watermark only counts lines that produced an event, so a transcript ending in lines it ignores reads
/// as unfinished forever; a mark taken at that same watermark says those lines were already sent.
/// A mark applies only while the server still answers the watermark it recorded, so a server that lost
/// or gained lines since falls back to its own watermark.
/// </summary>
internal sealed class TranscriptImportLedger {
    internal const string FileName = "transcript-imports.json";

    const           int      MaxIdleRounds   = 5;
    static readonly TimeSpan ConfirmBudget   = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ConfirmInterval = TimeSpan.FromSeconds(1);

    readonly ConfigRoot                                                          _config;
    readonly Dictionary<string, Dictionary<string, TranscriptImportMark>>        _marks;
    readonly Dictionary<(string Server, string SessionId), TranscriptImportMark> _pending     = [];
    readonly List<Delivered>                                                     _unconfirmed = [];
    readonly Lock                                                                _gate        = new();

    TranscriptImportLedger(ConfigRoot config, Dictionary<string, Dictionary<string, TranscriptImportMark>> marks) {
        _config = config;
        _marks  = marks;
    }

    public static TranscriptImportLedger Load(ConfigRoot config) => new(config, Read(config, out _) ?? Empty());

    /// <summary>A ledger that only records; <see cref="Save"/> merges into whatever the file holds then.</summary>
    public static TranscriptImportLedger ForRecording(ConfigRoot config) => new(config, Empty());

    /// <summary>The line to resume from when the server's watermark is still the one the mark recorded.</summary>
    public int? ResumeFrom(string server, string sessionId, int watermark) => MatchingMark(server, sessionId, watermark)?.Lines;

    public async Task<TranscriptImportMark?> MatchFileAsync(string server, string sessionId, int watermark, string path, CancellationToken ct) {
        var mark = MatchingMark(server, sessionId, watermark);
        if (mark?.PrefixSha256 is not { } expected) return null;
        try {
            var actual = await TranscriptImportFingerprint.ReadPrefixAsync(path, mark.Lines, ct);
            return actual == expected ? mark : null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    TranscriptImportMark? MatchingMark(string server, string sessionId, int watermark) {
        lock (_gate) {
            return _marks.TryGetValue(server, out var sessions)
                && sessions.TryGetValue(sessionId, out var mark)
                && mark.Watermark == watermark
                && mark.Lines > watermark
                    ? mark
                    : null;
        }
    }

    public void Record(string server, string sessionId, TranscriptImportMark mark) {
        lock (_gate) _pending[(server, sessionId)] = mark;
    }

    /// <summary>Marks <paramref name="completeLines"/> as delivered at the server's current watermark. A watermark the
    /// server cannot answer yet is asked for again by <see cref="ConfirmAsync"/>.</summary>
    public async Task RecordAsync(HttpClient httpClient, string baseUrl, string sessionId, int completeLines, CancellationToken ct, string? prefixSha256 = null) {
        var delivered = new Delivered(baseUrl, sessionId, completeLines, prefixSha256);
        if (!await SettleAsync(httpClient, delivered, ct)) lock (_gate) _unconfirmed.Add(delivered);
    }

    /// <summary>
    /// Asks again for every watermark <see cref="RecordAsync"/> could not read. The server authorizes <c>last-line</c>
    /// against its session projection, which trails the event store: right after a burst of imports it answers 404 for
    /// sessions it has already stored, and without a mark those sessions resend their ignored tail on the next import.
    /// The first round runs at once; later ones wait and run only once a round has settled something, since a server that
    /// refuses every probe shows no sign of catching up and a 404 alone cannot say it ever will.
    /// </summary>
    public async Task ConfirmAsync(HttpClient httpClient, TimeProvider time, CancellationToken ct) {
        List<Delivered> waiting;
        lock (_gate) {
            waiting = [.. _unconfirmed];
            _unconfirmed.Clear();
        }

        var deadline   = time.GetUtcNow() + ConfirmBudget;
        var caughtUp   = false;
        var idleRounds = 0;

        for (var round = 0; waiting.Count > 0; round++) {
            if (round > 0) await Task.Delay(ConfirmInterval, time, ct);

            var unsettled = new List<Delivered>();
            await Parallel.ForEachAsync(waiting, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (d, token) => {
                if (!await SettleAsync(httpClient, d, token)) lock (unsettled) unsettled.Add(d);
            });

            if (unsettled.Count < waiting.Count) (caughtUp, idleRounds) = (true, 0);
            else idleRounds++;
            waiting = unsettled;

            if (!caughtUp || idleRounds >= MaxIdleRounds || time.GetUtcNow() >= deadline) break;
        }
    }

    /// <summary>False when the server's answer may still change to one a mark can be taken at.</summary>
    async Task<bool> SettleAsync(HttpClient httpClient, Delivered d, CancellationToken ct) {
        try {
            using var resp = await httpClient.GetAsync($"{d.Server}/api/sessions/{d.SessionId}/last-line", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound || HttpClientExtensions.IsRetryableStatus(resp.StatusCode)) return false;
            if (resp.StatusCode != HttpStatusCode.OK) return true;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.Num("last_line_number") is { } watermark && d.Lines > watermark)
                Record(d.Server, d.SessionId, new TranscriptImportMark(d.Lines, (int)watermark, d.PrefixSha256));

            return true;
        } catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !ct.IsCancellationRequested) {
            return false;
        } catch (Exception e) when (e is JsonException or InvalidOperationException) {
            return true;
        }
    }

    readonly record struct Delivered(string Server, string SessionId, int Lines, string? PrefixSha256);

    /// <summary>Merges this run's marks into the file. Best effort: a mark that never lands costs only a
    /// resend on the next import.</summary>
    public void Save() {
        lock (_gate) {
            if (_pending.Count == 0) return;

            try {
                using (_config.AcquireLock(FileName)) {
                    // A file that exists but cannot be read is left alone rather than replaced by this run's marks.
                    var onDisk = Read(_config, out var unreadable) ?? (unreadable ? null : Empty());
                    if (onDisk is null) return;

                    foreach (var ((server, sessionId), mark) in _pending) {
                        if (!onDisk.TryGetValue(server, out var sessions)) onDisk[server] = sessions = new(StringComparer.Ordinal);
                        sessions[sessionId] = mark;
                    }

                    Directory.CreateDirectory(_config.Directory);
                    AtomicFile.Replace(
                        _config.Path(FileName),
                        JsonSerializer.Serialize(onDisk, TranscriptImportLedgerJsonContext.Default.DictionaryStringDictionaryStringTranscriptImportMark));
                }

                _pending.Clear();
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or AbandonedMutexException) { }
        }
    }

    static Dictionary<string, Dictionary<string, TranscriptImportMark>> Empty() => new(StringComparer.Ordinal);

    static Dictionary<string, Dictionary<string, TranscriptImportMark>>? Read(ConfigRoot config, out bool unreadable) {
        unreadable = false;

        try {
            var json  = File.ReadAllText(config.Path(FileName));
            var marks = JsonSerializer.Deserialize(json, TranscriptImportLedgerJsonContext.Default.DictionaryStringDictionaryStringTranscriptImportMark);

            return marks?
                .Where(s => s.Value is not null)
                .ToDictionary(
                    s => s.Key,
                    s => s.Value.Where(m => m.Value is not null).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal);
        } catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException) {
            return null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            unreadable = true;

            return null;
        }
    }
}
