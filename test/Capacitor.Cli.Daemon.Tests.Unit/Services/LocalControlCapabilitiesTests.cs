using Capacitor.Cli.Daemon.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The advertised capability list, pinned whole, so a new entry lands only with a deliberate edit here:
/// each must be backed by a routed handler or by status fields the daemon fills, never reach a client
/// that then asks for behavior the daemon has not got.
/// </summary>
public class LocalControlCapabilitiesTests {
    /// <summary>The steering switch is left out on Windows, where steering isn't offered.</summary>
    [Test]
    public async Task The_advertised_list_is_pinned_whole() {
        string[] expected = [
            "consent/1", "consent/2", "consent/3", "status/1", "permission/1", "input/1", "input/2", "settings/1", "fence/1", "shutdown/1", "shutdown/2", "restart/2", "resume/1", "attach-runtime/1", "agent-session/1", "accounts/1", "experimental/1", "rename/1", "handoff/1", "headless/1", "resume-in-place/1", "handoff-local/1", "recording-disable/1",
            .. OperatingSystem.IsWindows() ? [] : (string[])["steering-switch/1"]
        ];

        await Assert.That(LocalControlCapabilities.Current).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>Both hand-overs reach handlers only while recording is on, and neither is said otherwise.</summary>
    [Test]
    public async Task Recording_adds_the_hosted_and_terminal_hand_overs_and_nothing_else() {
        await Assert.That(LocalControlCapabilities.Advertised(true))
            .IsEquivalentTo([.. LocalControlCapabilities.Current, "recording/1", "recording-terminal/1"], CollectionOrdering.Matching);
        await Assert.That(LocalControlCapabilities.Advertised(false)).IsEquivalentTo(LocalControlCapabilities.Current, CollectionOrdering.Matching);
    }
}
