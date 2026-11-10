namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>What <see cref="CodexHookTrustSeeder"/> did for one Codex home.</summary>
public enum CodexHookTrustSeed {
    /// <summary>Codex now trusts every kcap hook, and kcap wrote at least one of those trust entries.</summary>
    Trusted,

    /// <summary>Codex already trusted every kcap hook; nothing was written.</summary>
    AlreadyTrusted,

    /// <summary>Codex lists no kcap hook from this home's <c>hooks.json</c>, so there is nothing to trust.</summary>
    NoHooks,

    /// <summary>No Codex could be started to do the write.</summary>
    CodexUnavailable,

    /// <summary>Codex refused or did not answer, or still reports a kcap hook untrusted afterwards.</summary>
    Failed,
}
