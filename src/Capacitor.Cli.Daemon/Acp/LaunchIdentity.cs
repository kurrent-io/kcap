using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Generate each name once per launch and share it across MCP injection, argv, and assertions;
/// independent derivations can silently exclude the reviewer's own result channel.
/// Names must be unguessable because a repository can declare a server that impersonates any
/// predictable allowlisted name and have it spawned under the daemon user.
/// </summary>
internal sealed record LaunchIdentity {
    /// <summary>What every reserved-name comparison sees — <c>KcapMcpRegistry</c>'s reservation check and
    /// Copilot's tool-id builder. For an aliasing vendor this never reaches the wire.</summary>
    public string ResultChannelCanonicalId { get; }

    /// <summary>The result channel's name as the vendor sees it. Equal to
    /// <see cref="ResultChannelCanonicalId"/> for every vendor that does not need aliasing, so their
    /// behaviour is byte-identical to before this type existed.</summary>
    public string ResultChannelWireName { get; }

    /// <summary>A name nothing can match, used as the deny-all value of a vendor's MCP allowlist so an
    /// interactive launch admits no repository-authored server.</summary>
    public string UnmatchableMcpName { get; }

    readonly bool _aliases;
    readonly Guid _allowlistSuffix;

    // Private: a caller can ask for a fresh identity but cannot construct an arbitrary one. This is the
    // seam where a fixed or reused name would otherwise slip in, and every argv-equality test in the suite
    // would still pass while the containment was gone.
    private LaunchIdentity(string canonicalId, string wireName, string unmatchableName, bool aliases, Guid allowlistSuffix) {
        ResultChannelCanonicalId = canonicalId;
        ResultChannelWireName    = wireName;
        UnmatchableMcpName       = unmatchableName;
        _aliases                 = aliases;
        _allowlistSuffix         = allowlistSuffix;
    }

    /// <summary>
    /// Aliasing vendors use the launch-specific suffix for both injected MCP names and the exact allowlist.
    /// Admitting a canonical public name would let a repository impersonate a server spawned as the daemon user.
    /// Other vendors retain the canonical name.
    /// </summary>
    public string AllowlistWireName(string canonicalId) =>
        _aliases ? $"{canonicalId}-{_allowlistSuffix:N}" : canonicalId;

    /// <summary>
    /// The ONLY production entry point. Three INDEPENDENT v4 GUIDs, and no launch context in scope — so the
    /// names cannot be derived from a session id, an agent id, a worktree path or the clock even by
    /// accident, because none of those values is reachable from here.
    /// </summary>
    /// <param name="aliasResultChannel">True only for a vendor whose MCP gate is a name allowlist that must
    /// also admit our own channel, so the channel's name has to be unguessable too (Gemini today). False
    /// keeps the canonical id on the wire, which is what every other vendor has always sent.</param>
    public static LaunchIdentity ForLaunch(bool aliasResultChannel) =>
        FromGuids(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), aliasResultChannel);

    /// <summary>
    /// Use concrete Guid values for the test seam; a factory delegate could close over launch context
    /// and conceal a second derivation of the names that must be shared.
    /// </summary>
    internal static LaunchIdentity FromGuids(Guid resultChannel, Guid unmatchable, Guid allowlistSuffix, bool aliasResultChannel) {
        // No fallback, predictable or otherwise. The security property is that these strings are unguessable
        // at the instant the repository's own MCP declarations are read, so a degraded name is worse than no
        // agent at all. Independence is asserted rather than assumed: the wire name is visible to the agent
        // (it is in its own process argv), so a shared GUID would make the unmatchable name derivable from it.
        if (resultChannel == Guid.Empty || unmatchable == Guid.Empty || allowlistSuffix == Guid.Empty
         || resultChannel == unmatchable || resultChannel == allowlistSuffix || unmatchable == allowlistSuffix)
            throw new InvalidOperationException(
                "Refusing to build a launch identity: a generated name would be predictable or reused. A "
              + "vendor's MCP allowlist is an exact-name gate, so any of these names being guessable lets "
              + "the repository being worked in impersonate an injected review server.");

        return new(
            KcapMcpRegistry.ReservedResultChannelId,
            aliasResultChannel
                ? $"{KcapMcpRegistry.ReservedResultChannelId}-{resultChannel:N}"
                : KcapMcpRegistry.ReservedResultChannelId,
            $"kcap-deny-{unmatchable:N}",
            aliasResultChannel,
            allowlistSuffix);
    }
}
