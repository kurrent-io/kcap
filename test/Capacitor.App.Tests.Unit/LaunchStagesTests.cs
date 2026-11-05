using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// <summary>The starting line names the harness as the launcher does, not by its vendor token.</summary>
public class LaunchStagesTests {
    [Test]
    [Arguments("mistral-vibe", "Starting Mistral Vibe · Launch accepted")]
    [Arguments("opencode", "Starting OpenCode · Launch accepted")]
    [Arguments("newvendor", "Starting Newvendor · Launch accepted")]
    [Arguments("", "Starting agent · Launch accepted")]
    public async Task StartingText_names_the_harness(string vendor, string expected) =>
        await Assert.That(LaunchStages.StartingText(vendor, null)).IsEqualTo(expected);
}
