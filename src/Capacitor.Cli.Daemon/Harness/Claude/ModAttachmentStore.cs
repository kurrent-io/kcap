using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Skills;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>The steering state file: owner-only, in the daemon's owner-only state directory, replaced whole and flushed
/// before the rename, because a report it holds may be the only record left that a dispatch landed.</summary>
internal sealed partial class ModAttachmentStore(string path, ILogger logger) {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The saved state; empty when there is none, and when it can't be read, which is logged. A file that parses
    /// is read entry by entry: one missing what it needs, as another build might write, is dropped and the rest kept.
    /// Nothing enforces non-null on deserialization, so every entry is checked here.</summary>
    public ModAttachmentFile Load() {
        ModAttachmentFile? file;

        try {
            if (!File.Exists(path)) return ModAttachmentFile.Empty;

            file = JsonSerializer.Deserialize(File.ReadAllBytes(path), ModAttachmentJsonContext.Default.ModAttachmentFile);
        } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) {
            return Unreadable(ex.Message);
        }

        if (file is null) return Unreadable("it holds nothing");

        var dropped = 0;

        var attachments = Keep(file.Attachments, IsValid, ref dropped);
        var reports     = Keep(file.Reports, IsValid, ref dropped);
        var stopped     = Keep(file.Stopped, s => s is { SessionId.Length: > 0, ProcessKey.Length: > 0 }, ref dropped);
        var handedOver  = Keep(file.HandedOver, (id, h) => Guid.TryParse(id, out _) && IsValid(h), ref dropped);
        var replaced    = Keep(file.Replaced, (id, by) => ModAttachments.IsAgentId(id) && by is { Length: > 0 }, ref dropped);
        var carriedFrom = Keep(file.CarriedFrom, (id, from) => ModAttachments.IsAgentId(id) && ModAttachments.IsAgentId(from), ref dropped);
        var reported    = Keep(file.Reported, id => Guid.TryParse(id, out var dispatchId) && dispatchId != Guid.Empty, ref dropped);

        if (dropped > 0) LogEntriesDropped(path, dropped);

        return new ModAttachmentFile(attachments, handedOver, reports, replaced, stopped, carriedFrom, reported);
    }

    static bool IsValid(ModAttachmentRecord? a) =>
        a is not null && Guid.TryParse(a.SessionId, out _) && a.ProcessKey is { Length: > 0 }
     && a.AgentId == ModAttachments.AgentIdFor(SessionIds.Canonical(a.SessionId)!)
     && (a.ReplacesAgentId is null || ModAttachments.IsAgentId(a.ReplacesAgentId));

    static bool IsValid(ModHandover? h) => h is not null && ModAttachments.IsAgentId(h.AgentId) && h.ProcessKey is { Length: > 0 };

    static bool IsValid(ModInputReport? r) =>
        r is not null && r.DispatchId != Guid.Empty && ModAttachments.IsAgentId(r.AgentId) && r.Refusals >= 0 && r.Kind switch {
            ModInputReport.Link                                   => r.SessionId is { Length: > 0 } && r.RowUuid is { } row && row != Guid.Empty,
            ModInputReport.Rejected or ModInputReport.Unconfirmed => r.Reason is { Length: > 0 },
            _                                                     => false,
        };

    static T[] Keep<T>(T[]? entries, Func<T?, bool> valid, ref int dropped) where T : class {
        if (entries is null) return [];

        T[] kept = [.. entries.Where(e => valid(e))];
        dropped += entries.Length - kept.Length;

        return kept;
    }

    static Dictionary<string, T> Keep<T>(Dictionary<string, T>? entries, Func<string, T?, bool> valid, ref int dropped) where T : class {
        if (entries is null) return [];

        var kept = entries.Where(e => valid(e.Key, e.Value)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        dropped += entries.Count - kept.Count;

        return kept;
    }

    ModAttachmentFile Unreadable(string why) {
        LogUnreadable(path, why);

        return ModAttachmentFile.Empty;
    }

    public bool TrySave(ModAttachmentFile file) {
        try {
            OwnerOnlyDirectory.Ensure(Path.GetDirectoryName(path)!);
            AtomicFile.Replace(path, JsonSerializer.SerializeToUtf8Bytes(file, ModAttachmentJsonContext.Default.ModAttachmentFile),
                OwnerOnly, followLink: false, durable: true);

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            LogUnwritable(path, ex.Message);

            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steering state at {Path} can't be read, so it starts empty: {Why}")]
    partial void LogUnreadable(string path, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steering state at {Path} had {Count} entries missing what they need; they were dropped")]
    partial void LogEntriesDropped(string path, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steering state at {Path} could not be saved: {Why}")]
    partial void LogUnwritable(string path, string why);
}
