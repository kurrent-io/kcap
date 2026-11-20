using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Codex;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>Builds the canonical batch for redacted transcript lines of one stream, and keeps the projection's state
/// between batches. An amendment lands on its target when the target is in the same batch and is dropped otherwise; a
/// usage stamps its targets in the same batch and is restated in a backfill event for targets the server already
/// acknowledged. That is what the server stores for the same lines sent raw.</summary>
public sealed class CanonicalBatchAssembler(ITranscriptProjection projection, ILineSignalReader signals, string sessionId, string? agentId, string vendor) {
    readonly TranscriptContext _context = projection.CreateContext(sessionId, agentId);

    readonly HashSet<Guid> _acknowledged = [];

    Guid[] _assembled = [];

    bool _assembledEntries;

    /// <summary>Projects a raw line the server already holds and discards the output, so the lines after it project as
    /// one uninterrupted run would project them. Each replayed line is a batch of its own, acknowledged. A line its
    /// capture replaced with a capture-loss line projects nothing, as on the way out.</summary>
    /// <param name="decide">Runs the redaction on a line the rule cannot decide on its own; without it such a line stays
    /// undecided.</param>
    /// <returns>False for a line whose capture stays undecided, which leaves the replay unable to continue.</returns>
    public bool Replay(string line, int lineNumber, DateTimeOffset receivedAt, LineCaptureDecider? decide = null, CancellationToken ct = default) {
        if (CaptureLossLine.Is(line)) return true;

        var capture = CaptureRule.Of(line);
        if (capture == LineCapture.Undecided && decide is not null) capture = decide(line, ct);

        switch (capture) {
            case LineCapture.Lost:      return true;
            case LineCapture.Undecided: return false;
        }

        var batch = new Batch();
        Project(line, lineNumber, receivedAt, batch);
        Persisted([.. batch.Events.Select(e => e.Event.EventId)], batch.Events.Count > 0);

        return true;
    }

    public CanonicalTranscriptBatch Assemble(TranscriptBatch raw, DateTimeOffset receivedAt) {
        if (raw.LineNumbers is not { } numbers || numbers.Length != raw.Lines.Length)
            throw new ArgumentException("A canonical batch needs a line number for every line", nameof(raw));

        var batch = new Batch();
        var lines = new CanonicalBatchLine[raw.Lines.Length];
        List<CanonicalBatchTitle>?       titles      = null;
        List<CanonicalBatchCaptureLoss>? captureLoss = null;
        int?                             turnEnd     = null;

        for (var i = 0; i < raw.Lines.Length; i++) {
            var (line, number) = (raw.Lines[i], numbers[i]);
            var read = signals.Read(line, sessionId, agentId);
            lines[i] = new(number, read.Uuid, read.ParentUuid, read.RecordType, read.Timestamp);

            if (CaptureLossLine.Is(line)) {
                (captureLoss ??= []).Add(new(number, line));
                continue;
            }

            if (!Project(line, number, receivedAt, batch)) continue;

            if (read.Title is { } title) (titles ??= []).Add(new(title.Kind, title.Title, title.OwnTime, title.Legacy, number));
            if (read.EndsTurn) turnEnd = Math.Max(turnEnd ?? number, number);
        }

        _assembled        = [.. batch.Events.Select(e => e.Event.EventId)];
        _assembledEntries = batch.Events.Count > 0 || captureLoss is not null;

        return new() {
            SessionId       = sessionId,
            AgentId         = agentId,
            Vendor          = vendor,
            Lines           = lines,
            Events          = [.. batch.Events.Select(Wire)],
            Titles          = titles?.ToArray(),
            TurnEndLine     = turnEnd,
            CaptureLoss     = captureLoss?.ToArray(),
            Repository      = raw.Repository,
            Strict          = raw.Strict,
            ObservedCommits = raw.ObservedCommits,
            MoreFollows     = raw.MoreFollows,
        };
    }

    /// <summary>The server stored every event of the last assembled batch.</summary>
    public void Acknowledged() {
        Persisted(_assembled, _assembledEntries);
        _assembled        = [];
        _assembledEntries = false;
    }

    /// The server's pipeline clears a projection's per-batch state only after a batch that stored something, so a
    /// record waiting for a later line, such as a Codex exec end for its output, outlives a batch that stored nothing.
    void Persisted(Guid[] eventIds, bool storedEntries) {
        foreach (var id in eventIds) {
            _context.MarkPersisted(id);
            _acknowledged.Add(id);
        }

        if (storedEntries) _context.BeginBatch();
    }

    /// False for a line the projection rejected, which the batch covers and nothing else.
    bool Project(string line, int lineNumber, DateTimeOffset receivedAt, Batch batch) {
        var result = projection.Project(line, lineNumber, receivedAt, _context);
        if (result.Rejected is not null) return false;

        List<Pending>? backfills = null;
        foreach (var e in result.Events) {
            if (e.Payload is UsageApplied applied) {
                if (Apply(applied, e, lineNumber, batch) is { } backfill) (backfills ??= []).Add(backfill);
                continue;
            }

            batch.Latest[e.EventId] = batch.Events.Count;
            batch.Events.Add(new(e, e.LineNumber ?? lineNumber));
        }

        foreach (var amendment in result.Amendments) {
            if (!batch.Latest.TryGetValue(amendment.TargetEventId, out var target)
             || SchemaExtensions.Of(batch.Events[target].Event.Payload) is not { } extensions) continue;

            if (extensions.TryGetValue(amendment.Slug, out var block))
                foreach (var (field, value) in amendment.Extension.Fields) block.Fields[field] = value.Clone();
            else
                extensions[amendment.Slug] = amendment.Extension.Clone();
        }

        // A backfill follows the events of its line, as the server queues it.
        if (backfills is not null) batch.Events.AddRange(backfills);

        return true;
    }

    /// Stamps the usage on every target in the batch. When any target was acknowledged earlier, every stamp is an echo
    /// and the usage is restated for the acknowledged targets in a backfill under the usage's own id.
    Pending? Apply(UsageApplied applied, CanonicalEvent usage, int lineNumber, Batch batch) {
        var acknowledged = applied.Targets.Where(t => _acknowledged.Contains(t.EventId)).ToArray();

        foreach (var target in applied.Targets) {
            if (!batch.Latest.TryGetValue(target.EventId, out var index)) continue;

            var pending = batch.Events[index];
            batch.Events[index] = pending with { Event = Stamp(pending.Event, applied, target.IsEcho || acknowledged.Length > 0) };
        }

        return acknowledged.Length == 0 ? null : new(Backfill(applied, usage, acknowledged), lineNumber);
    }

    /// As the server stamps stored metadata: the usage and its echo flag replace the earlier ones, and a cache-creation
    /// count replaces the earlier one only when it is positive.
    static CanonicalEvent Stamp(CanonicalEvent e, UsageApplied applied, bool isEcho) => e with {
        Usage       = applied.Usage,
        UsageIsEcho = isEcho,
        CacheCreationTokens = applied.CacheCreationTokens is > 0 ? applied.CacheCreationTokens
            : e.Usage is not null && e.CacheCreationTokens is > 0 ? e.CacheCreationTokens
            : null,
    };

    static CanonicalEvent Backfill(UsageApplied applied, CanonicalEvent usage, UsageTarget[] acknowledged) {
        var counts = applied.Usage;
        long? window = counts.AdditionalCounts?.Fields.TryGetValue("model_context_window", out var w) == true ? (long)w.NumberValue : null;

        var payload = new CodexUsageBackfilledEvent {
            Model              = counts.HasModel ? counts.Model : null,
            InputTokens        = counts.InputTokens,
            OutputTokens       = counts.OutputTokens,
            CacheReadTokens    = counts.CachedInputTokens,
            CacheWriteTokens   = applied.CacheCreationTokens,
            ReasoningTokens    = counts.HasReasoningTokens ? counts.ReasoningTokens : null,
            // A window reported as zero or less is an unknown one.
            ModelContextWindow = window is > 0 ? window : null,
            Targets = [
                .. acknowledged.Select(t => new CodexUsageBackfillTarget {
                    EventId = t.EventId.ToString("N"), EventType = t.EventType, ToolName = t.ToolName, IsEcho = t.IsEcho,
                }),
            ],
        };

        return new(CanonicalEventTypes.CodexUsageBackfilled, payload, usage.EventId, usage.Timestamp, usage.RecordTimestamp);
    }

    static CanonicalBatchEvent Wire(Pending pending) {
        var e = pending.Event;

        return new() {
            Type                = e.EventType,
            Payload             = Element(CanonicalEventJson.Write(e.Payload)),
            EventId             = e.EventId,
            Line                = pending.Line,
            Timestamp           = e.Timestamp,
            RecordTimestamp     = e.RecordTimestamp,
            CausedBy            = e.CausedBy,
            Usage               = e.Usage is { } usage ? Element(CanonicalEventJson.WriteUsage(usage)) : null,
            UsageIsEcho         = e.UsageIsEcho,
            CacheCreationTokens = e.CacheCreationTokens,
            VendorMetadata      = VendorMetadata(e),
            Attachments = e.Attachments is { Count: > 0 } attachments
                ? [.. attachments.Select(a => new CanonicalBatchAttachment(a.Id, a.MessageId, a.BlockIndex, a.FileName, a.ContentType, a.Data))]
                : null,
        };
    }

    static JsonElement Element(string json) {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// Both of the event's vendor maps in one; a key in both takes the typed value.
    static Dictionary<string, JsonElement>? VendorMetadata(CanonicalEvent e) {
        Dictionary<string, JsonElement>? metadata = null;

        if (e.VendorMetadata is { } strings)
            foreach (var (key, value) in strings)
                (metadata ??= new(StringComparer.Ordinal))[key] = JsonSerializer.SerializeToElement(value, CapacitorJsonContext.Default.String);

        if (e.VendorMetadataValues is { } values)
            foreach (var (key, value) in values) {
                (metadata ??= new(StringComparer.Ordinal))[key] = value switch {
                    long number => JsonSerializer.SerializeToElement(number, CapacitorJsonContext.Default.Int64),
                    bool flag   => JsonSerializer.SerializeToElement(flag, CapacitorJsonContext.Default.Boolean),
                    _           => throw new ArgumentException($"Vendor metadata {key} is a {value.GetType().Name}, not a long or a bool", nameof(e)),
                };
            }

        return metadata;
    }

    /// The events of one batch in order, and the position of the latest event under each id: the one an amendment or a
    /// usage lands on.
    sealed class Batch {
        public List<Pending>         Events { get; } = [];
        public Dictionary<Guid, int> Latest { get; } = [];
    }

    readonly record struct Pending(CanonicalEvent Event, int Line);
}
