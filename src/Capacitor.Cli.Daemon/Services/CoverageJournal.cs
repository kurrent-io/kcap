using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// A boot is cumulatively covered only if the prior journal was covered, its instance id matches
/// the preceding lock-file nonce, and this epoch is contained. Missing, corrupt, or unwritable state
/// fails closed; persisting false makes that verdict sticky across later boots.
/// Store Initialized and the verdict in one temp-and-rename document: separate writes could leave
/// a crash looking like genesis and silently reset coverage. An absent journal is the genesis boundary.
/// Only deleting both the state directory and per-name lock permits an operator reset.
/// </summary>
internal sealed partial class CoverageJournal(string stateDir, ILogger logger) {
    readonly string _journalPath = Path.Combine(stateDir, "coverage.json");

    readonly record struct Journal(bool Initialized, string InstanceId, bool CumulativeCovered);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(Journal))]
    partial class Ctx : JsonSerializerContext;

    /// <summary>Fold this boot and persist the new journal atomically BEFORE Connect/spawn. Returns
    /// cumulative_covered. Never throws — an I/O failure returns false (fail-closed).</summary>
    public bool RecordBoot(string myInstanceId, string? priorLockInstanceId, bool priorLockReadFailed, bool thisEpochContained) {
        try {
            var journalExists = File.Exists(_journalPath);
            var prior         = journalExists ? ReadJournal() : null; // null ⇒ present-but-corrupt

            bool covered;
            if (!journalExists) {
                // Genesis is the ONLY seed-true base case: the journal file is absent (we are about to
                // atomically initialize it) AND the captured prior lock shows no pre-existing InstanceId
                // (a genuine first-ever boot for this name). A previously-used dir whose journal is gone
                // but whose prior lock DOES carry an InstanceId is un-journaled history that re-pointing/
                // deleting the dir cannot launder ⇒ false. An INDETERMINATE prior lock (non-empty but
                // unreadable/garbage) is NOT genesis either — an unreadable prior could hide an intervening
                // boot, so it must fail-closed (never conflate "unreadable" with "genuinely empty").
                var genesis = !priorLockReadFailed && priorLockInstanceId is null;
                covered = genesis && thisEpochContained;
            } else if (prior is not { Initialized: true } t) {
                covered = false; // journal present but corrupt / not fully initialized -> fail-closed
            } else {
                var chainOk = priorLockInstanceId is { } pid && string.Equals(pid, t.InstanceId, StringComparison.Ordinal);
                covered = t.CumulativeCovered && chainOk && thisEpochContained;
            }

            WriteJournalAtomic(new Journal(true, myInstanceId, covered)); // persists the break in THIS entry (sticky)
            return covered;
        } catch (Exception ex) {
            logger.LogWarning(ex, "CoverageJournal: fold/persist failed — advertising RecordlessSurvivorsImpossible=false (fail-closed)");
            return false;
        }
    }

    Journal? ReadJournal() {
        try {
            return JsonSerializer.Deserialize(File.ReadAllText(_journalPath), Ctx.Default.Journal);
        } catch { return null; } // corrupt -> journal present but unparseable => false (never genesis)
    }

    void WriteJournalAtomic(Journal journal) {
        Directory.CreateDirectory(stateDir);
        var tmp = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, JsonSerializer.Serialize(journal, Ctx.Default.Journal));
        File.Move(tmp, _journalPath, overwrite: true);   // THE single atomic same-directory rename (marker folded in)
    }
}
