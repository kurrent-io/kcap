using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.MistralVibe;

namespace Capacitor.Cli.Tests.Unit.Harness.MistralVibe;

/// <summary>
/// A hook inside <c>vibe-acp</c> carries the dashed session id the ACP session reports; the daemon answers with the
/// canonical form it binds. Only a match on the agent the hook runs under stands it down.
/// </summary>
public class MistralVibeHostedSessionTests {
    const string AcpSession = "0d15ea90-e203-908f-6098-82a8cd39f172";

    static Func<string, string?> Env(string? agentId) =>
        name => name == HostedAgent.DaemonAgentIdVar ? agentId : null;

    [Test]
    public async Task The_session_the_daemon_hosts_is_daemon_hosted() {
        var asked = new List<string>();

        var hosted = MistralVibeHostedSession.IsDaemonHosted(
            AcpSession, Env("agent-1"), agentId => { asked.Add(agentId); return "0d15ea90e203908f609882a8cd39f172"; });

        await Assert.That(hosted).IsTrue();
        await Assert.That(asked).IsEquivalentTo(["agent-1"]);
    }

    /// <summary>A vibe the hosted agent starts inherits the agent id, but its session is its own.</summary>
    [Test]
    public async Task A_vibe_the_hosted_agent_starts_keeps_recording() =>
        await Assert.That(MistralVibeHostedSession.IsDaemonHosted(
            "a79b1578-a05d-81a7-ab0a-ff10b02a4434", Env("agent-1"), _ => AcpSession)).IsFalse();

    /// <summary>An unreachable daemon or an agent not yet bound answers null, which must not match a session.</summary>
    [Test]
    public async Task No_answer_from_the_daemon_keeps_recording() =>
        await Assert.That(MistralVibeHostedSession.IsDaemonHosted(AcpSession, Env("agent-1"), _ => null)).IsFalse();

    [Test]
    public async Task A_terminal_session_never_asks_a_daemon() {
        var asked = false;

        var hosted = MistralVibeHostedSession.IsDaemonHosted(AcpSession, Env(null), _ => { asked = true; return AcpSession; });

        await Assert.That(hosted).IsFalse();
        await Assert.That(asked).IsFalse();
    }
}
