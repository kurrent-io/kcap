using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Whether Codex runs kcap's hooks in a Codex home, from the answer the daemon's account refresh last
/// stored when it still describes the files on disk, else from asking Codex now.</summary>
public static class CodexHooksTrustCheck {
    /// <summary>The stored answer for <paramref name="paths"/>, or Unknown when there is none or the hooks file or
    /// <c>config.toml</c> changed after it was given. Never starts Codex.</summary>
    public static CodexHooksTrust Stored(AccountRegistry? registry, CodexPaths paths) {
        var account = registry?.Accounts.FirstOrDefault(a => a.Vendor == HarnessId.Codex && AccountDirectory.Same(a.Directory, paths.Home));
        if (account is not { HookTrust: { } trust, HookTrustFingerprint: { } stored }) return CodexHooksTrust.Unknown;
        if (CodexTrustFingerprint.Of(paths) != stored) return CodexHooksTrust.Unknown;

        return trust switch {
            HookTrustStates.Trusted   => CodexHooksTrust.Trusted,
            HookTrustStates.Untrusted => CodexHooksTrust.Untrusted,
            _                         => CodexHooksTrust.Unknown,
        };
    }

    /// <summary>What can be told without starting Codex: the stored answer, else Untrusted when kcap's hooks are in and
    /// <c>config.toml</c> records no trust at all. Codex keeps each trusted hook as a <c>trusted_hash</c> there, so
    /// none means none is trusted; a recorded hash is Codex's to check, which leaves it Unknown.</summary>
    public static CodexHooksTrust Offline(AccountRegistry? registry, CodexPaths paths) {
        if (!CodexHooksInstaller.ReferencesKcapHook(paths.UserHooksJson)) return CodexHooksTrust.Unknown;

        var stored = Stored(registry, paths);
        if (stored != CodexHooksTrust.Unknown) return stored;

        return RecordsNoTrust(paths.ConfigToml) ? CodexHooksTrust.Untrusted : CodexHooksTrust.Unknown;
    }

    /// <summary>Unknown without asking when kcap's hooks are not installed in <paramref name="paths"/>: there is
    /// nothing for Codex to trust.</summary>
    public static async Task<CodexHooksTrust> ReadAsync(
            AccountRegistry? registry, CodexPaths paths, CodexHookTrustProbe probe, CancellationToken ct) {
        if (!CodexHooksInstaller.ReferencesKcapHook(paths.UserHooksJson)) return CodexHooksTrust.Unknown;

        var offline = Offline(registry, paths);
        return offline != CodexHooksTrust.Unknown ? offline : await probe.ProbeAsync(paths, ct).ConfigureAwait(false);
    }

    static bool RecordsNoTrust(string configToml) {
        try {
            if (!File.Exists(configToml)) return true;

            using var stream = new FileStream(configToml, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return !reader.ReadToEnd().Contains("trusted_hash", StringComparison.Ordinal);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }
}
