using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Harness.Kiro;

/// <summary>
/// Check each initialization notification as it arrives: the protocol has no completion barrier,
/// and sampling can miss an extra server between observations. Test membership, not announce count;
/// Kiro can announce the same injected name repeatedly, and per-launch aliases prevent impersonation.
/// This detects failures of config suppression; it does not supply that containment boundary.
/// A vendor that omits extra-server notifications can defeat detection without changing these checks.
/// </summary>
internal sealed class KiroMcpSurfaceMonitor {
    internal const string InitializedMethod = "_kiro.dev/mcp/server_initialized";
    internal const string InitFailureMethod = "_kiro.dev/mcp/server_init_failure";

    readonly IReadOnlySet<string> _injected;
    readonly string _resultChannelWireName;
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly object _gate = new();

    string? _violation;

    internal KiroMcpSurfaceMonitor(IReadOnlySet<string> injectedWireNames, string resultChannelWireName) {
        _injected = injectedWireNames;
        _resultChannelWireName = resultChannelWireName;
    }

    /// <summary>The coded failure, or null while the surface is still exactly the injected set.
    /// Sticky: the first violation is the one reported, because a later one may be a consequence.</summary>
    internal string? Violation { get { lock (_gate) return _violation; } }

    /// <summary>
    /// Whether the result channel has actually started. A readiness check, NOT the containment check
    /// — requiring it catches only TOTAL silence, never the selective omission described above.
    /// </summary>
    internal bool ResultChannelReady {
        get { lock (_gate) return _seen.Contains(_resultChannelWireName); }
    }

    /// <summary>Judged on arrival, on the connection's own read loop.</summary>
    internal void Observe(AcpNotification notification) {
        if (notification.Method is not (InitializedMethod or InitFailureMethod)) return;
        if (notification.Params is not { } payload) return;
        if (!payload.TryGetProperty("serverName", out var nameElement)) return;
        if (nameElement.GetString() is not { Length: > 0 } serverName) return;

        lock (_gate) {
            if (_violation is not null) return;   // sticky

            if (notification.Method == InitFailureMethod) {
                // Only the result channel's failure is fatal: without it the reviewer cannot report at
                // all, and the round would otherwise die as a silent timeout with no diagnosis.
                if (string.Equals(serverName, _resultChannelWireName, StringComparison.Ordinal))
                    _violation =
                        "kiro_reviewer_result_channel_unavailable: the injected result channel failed to "
                      + "start, so the reviewer has no way to deliver a result. Failing the round now "
                      + "rather than letting it time out with no diagnosis.";
                return;
            }

            if (!_injected.Contains(serverName)) {
                _violation =
                    $"kiro_reviewer_mcp_surface_unexpected: MCP server '{serverName}' initialized but is "
                  + "not one this launch injected. The reviewer's callable surface must be exactly the "
                  + "injected set, so the isolated KIRO_HOME is no longer suppressing the operator's "
                  + "global servers.";
                return;
            }

            // Recorded for readiness, never counted: kiro-cli (2.16.0) announces one injected server's
            // initialization more than once, and an injected name is a per-launch GUID nothing else can
            // stand up under — see the class doc.
            _seen.Add(serverName);
        }
    }

    /// <summary>The wire names a launch's injected specs carry — the monitor's expected set.</summary>
    internal static IReadOnlySet<string> InjectedNames(IReadOnlyList<AcpMcpServerSpec> injected) =>
        injected.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Builds a monitor for a review launch, or null when this vendor/launch is not one.</summary>
    internal static KiroMcpSurfaceMonitor? For(
            AcpVendorDescriptor descriptor, bool isReviewFlow,
            IReadOnlyList<AcpMcpServerSpec>? injected, LaunchIdentity? identity) =>
        isReviewFlow
     && descriptor.Vendor == AcpVendorDescriptors.Kiro.Vendor
     && injected is { Count: > 0 }
     && identity is not null
            ? new KiroMcpSurfaceMonitor(InjectedNames(injected), identity.ResultChannelWireName)
            : null;
}
