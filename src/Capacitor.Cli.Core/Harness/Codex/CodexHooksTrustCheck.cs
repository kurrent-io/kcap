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

    /// <summary>Unknown without asking when kcap's hooks are not installed in <paramref name="paths"/>: there is
    /// nothing for Codex to trust.</summary>
    public static async Task<CodexHooksTrust> ReadAsync(
            AccountRegistry? registry, CodexPaths paths, CodexHookTrustProbe probe, CancellationToken ct) {
        if (!CodexHooksInstaller.ReferencesKcapHook(paths.UserHooksJson)) return CodexHooksTrust.Unknown;

        var stored = Stored(registry, paths);
        return stored != CodexHooksTrust.Unknown ? stored : await probe.ProbeAsync(paths, ct).ConfigureAwait(false);
    }
}
