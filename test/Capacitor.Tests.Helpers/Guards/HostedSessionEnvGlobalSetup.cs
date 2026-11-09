using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Tests.Helpers.Guards;

/// <summary>
/// Assembly-wide unset of what a hosting daemon or a recorded session exports into an agent. A suite
/// run from inside a hosted agent otherwise resolves that agent's daemon name, profile and server
/// through the real override chain, and a test expecting its fixture's values fails only there.
/// </summary>
public class HostedSessionEnvGlobalSetup {
    static readonly List<(string Key, string? Inherited)> Cleared = [];

    static readonly string[] Variables = [
        HostedAgent.AgentIdVar,
        HostedAgent.RenderedVar,
        HostedAgent.BridgeUrlVar,
        HostedAgent.SessionKindVar,
        HostedAgent.FlowRoleVar,
        HostedAgent.DaemonEpochVar,
        HostedAgent.DaemonIdVar,
        HostedAgent.HostedAppServerVar,
        HostedAgent.CodexAutoReviewVar,
        HostedAgent.SkipVar,
        HostedAgent.DaemonAgentIdVar,
        DaemonNameResolver.NameVar,
        ProfileOverrides.UrlVar,
        ProfileOverrides.ProfileVar,
        "KCAP_SESSION_ID",
    ];

    [BeforeEvery(Assembly)]
    public static void ClearHostedSessionEnv() {
        Cleared.Clear();
        foreach (var key in Variables) {
            Cleared.Add((key, Environment.GetEnvironmentVariable(key)));
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [AfterEvery(Assembly)]
    public static void RestoreHostedSessionEnv() {
        foreach (var (key, inherited) in Cleared) Environment.SetEnvironmentVariable(key, inherited);
        Cleared.Clear();
    }
}
