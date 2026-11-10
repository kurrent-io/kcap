using System.Text.Json.Nodes;
using Tomlyn.Model;

namespace Capacitor.Cli.Core.Harness.Codex;

public static partial class CodexConfigToml {
    const string HookTrustClaimKey = "hook_trust";

    static readonly string[] HookStatePath   = ["hooks", "state"];
    static readonly string[] HookTrustFields = ["trusted_hash", "trusted_content_hash"];

    /// <summary>Whether kcap holds a claim on any Codex hook trust entry in <paramref name="configPath"/>.</summary>
    public static bool HasHookTrustClaim(string configPath) {
        try {
            var ledgerPath = LedgerPath(CanonicalConfigPath(configPath));

            return ReadOwnershipLedger(ledgerPath)?[HookTrustClaimKey] is JsonObject { Count: > 0 };
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="codexWrite"/>, Codex persisting trust for the hook keys in <paramref name="written"/>,
    /// under kcap's config lock, then claims the trust values that landed for those keys. A claim whose values have
    /// changed since is relinquished. A claimed key not in <paramref name="current"/> (kcap's hooks as Codex lists
    /// them now) has its values removed while they are still the claimed ones. Throws when the lock, ledger or
    /// config cannot be read or written.
    /// </summary>
    internal static async Task<Change> TrustHooksAsync(
            string configPath, IReadOnlySet<string> written, IReadOnlySet<string> current, Func<Task>? codexWrite) {
        var canonical = CanonicalConfigPath(configPath);

        // Only the cross-process lock: the in-process one cannot be held across an await, and every in-process
        // writer takes the cross-process lock too.
        using var crossProcess = AcquireConfigLock(canonical);

        var ledgerPath = LedgerPath(canonical);
        var ledger     = ReadOwnershipLedger(ledgerPath);

        // A ledger that exists but cannot be read may be the only proof of what kcap wrote, so it is never replaced.
        if (ledger is null && File.Exists(ledgerPath))
            throw new InvalidDataException($"{ledgerPath} cannot be read; Codex hook trust left as it is.");

        if (codexWrite is not null) await codexWrite().ConfigureAwait(false);

        var claims = ledger?[HookTrustClaimKey] as JsonObject;

        if (written.Count == 0 && claims is null) return Change.Unchanged;

        ledger ??= NewOwnershipLedger();
        if (claims is null) ledger[HookTrustClaimKey] = claims = new JsonObject();

        var root   = LoadToml(canonical);
        var states = LookupTable(root, HookStatePath);
        var stale  = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        foreach (var (key, claim) in claims.ToArray()) {
            if (written.Contains(key)) continue;

            if (!StillAsClaimed(states, key, claim)) claims.Remove(key);
            else if (!current.Contains(key)) {
                stale[key] = claim;
                claims.Remove(key);
            }
        }

        foreach (var key in written) {
            if (ClaimOf(states, key) is { } claim) claims[key] = claim;
            else claims.Remove(key);
        }

        if (claims.Count == 0) ledger.Remove(HookTrustClaimKey);

        // Config first, claim second for what Codex wrote; claim released first for what kcap removes. Either crash
        // leaves an unclaimed entry, which uninstall keeps.
        WriteJsonAtomic(ledgerPath, ledger);

        if (stale.Count == 0) return Change.Updated;

        RemoveClaimedTrust(canonical, root, states!, ledger, ledgerPath, stale);

        return Change.Updated;
    }

    /// <summary>
    /// Removes the hook trust kcap claimed in <paramref name="configPath"/> whose values are still the ones it
    /// claimed, and relinquishes every claim. Trust kcap did not write, or that changed after it did, stays.
    /// </summary>
    public static Change ReleaseHookTrust(string configPath) {
        lock (_writeLock) {
            try {
                var canonical  = CanonicalConfigPath(configPath);
                var ledgerPath = LedgerPath(canonical);
                if (!File.Exists(ledgerPath)) return Change.Unchanged;

                using var crossProcess = AcquireConfigLock(canonical);

                if (ReadOwnershipLedger(ledgerPath) is not { } ledger || ledger[HookTrustClaimKey] is not JsonObject claims)
                    return Change.Unchanged;

                var root      = LoadToml(canonical);
                var states    = LookupTable(root, HookStatePath);
                var removable = claims.Where(c => StillAsClaimed(states, c.Key, c.Value))
                                      .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
                var preserved = removable.Count < claims.Count;

                ledger.Remove(HookTrustClaimKey);
                WriteJsonAtomic(ledgerPath, ledger);

                if (removable.Count == 0) return preserved ? Change.PreservedUnownedEntries : Change.Unchanged;

                RemoveClaimedTrust(canonical, root, states!, ledger, ledgerPath, removable);

                return preserved ? Change.UpdatedWithPreservedEntries : Change.Updated;
            } catch {
                return Change.Failed;
            }
        }
    }

    /// <summary>The caller has already relinquished <paramref name="released"/>; a failed config write hands those
    /// claims back, since the config still holds what they describe.</summary>
    static void RemoveClaimedTrust(string canonical, TomlTable root, TomlTable states, JsonObject ledger, string ledgerPath,
                                   IReadOnlyDictionary<string, JsonNode?> released) {
        try {
            foreach (var key in released.Keys) {
                if (!states.TryGetValue(key, out var value) || value is not TomlTable entry) continue;

                foreach (var field in HookTrustFields) entry.Remove(field);
                if (entry.Count == 0) states.Remove(key);
            }

            if (states.Count == 0 && LookupTable(root, ["hooks"]) is { } hooks) {
                hooks.Remove("state");
                if (hooks.Count == 0) root.Remove("hooks");
            }

            WriteTomlAtomic(canonical, root);
        } catch {
            if (ledger[HookTrustClaimKey] is not JsonObject claims) ledger[HookTrustClaimKey] = claims = new JsonObject();
            foreach (var (key, claim) in released) claims[key] = claim?.DeepClone();
            try { WriteJsonAtomic(ledgerPath, ledger); } catch { /* the original failure is the one to report */ }

            throw;
        }
    }

    static JsonObject? ClaimOf(TomlTable? states, string key) {
        if (states is null || !states.TryGetValue(key, out var value) || value is not TomlTable entry) return null;
        if (TrustField(entry, "trusted_hash") is null) return null;

        var claim = new JsonObject();
        foreach (var field in HookTrustFields)
            if (TrustField(entry, field) is { } hash) claim[field] = hash;

        return claim;
    }

    static bool StillAsClaimed(TomlTable? states, string key, JsonNode? claim) =>
        claim is JsonObject claimed
     && states is not null && states.TryGetValue(key, out var value) && value is TomlTable entry
     && TrustField(entry, "trusted_hash") is not null
     && HookTrustFields.All(f => TrustField(entry, f) == StringField(claimed, f));

    static string? TrustField(TomlTable entry, string field) =>
        entry.TryGetValue(field, out var value) && value is string text ? text : null;
}
