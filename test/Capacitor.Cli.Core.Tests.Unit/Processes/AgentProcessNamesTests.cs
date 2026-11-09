using Capacitor.Cli.Core.Processes;

namespace Capacitor.Cli.Core.Tests.Unit.Processes;

public class AgentProcessNamesTests {
    [Test]
    [Arguments("/home/me/.local/share/claude/versions/2.1.272")]
    [Arguments("/Users/me/.local/share/claude/versions/2.1.272")]
    [Arguments(@"C:\Users\me\AppData\Local\claude\versions\2.1.272.exe")]
    public async Task Candidates_names_the_agent_above_a_versions_directory(string execPath) {
        // With a native install the basename IS the version, so only the directory above
        // versions/ names the agent.
        var candidates = AgentProcessNames.Candidates(execPath).ToList();

        await Assert.That(candidates.Contains("claude")).IsTrue();
    }

    [Test]
    public async Task Candidates_yields_the_basename_first() {
        var candidates = AgentProcessNames.Candidates("/home/me/.local/share/claude/versions/2.1.272").ToList();

        await Assert.That(candidates.Count).IsEqualTo(2);
        await Assert.That(candidates[0]).IsEqualTo("2.1.272");
        await Assert.That(candidates[1]).IsEqualTo("claude");
    }

    [Test]
    [Arguments("/usr/bin/claude")]
    [Arguments("claude")]
    public async Task Candidates_yields_only_the_basename_for_an_ordinary_path(string execPath) {
        var candidates = AgentProcessNames.Candidates(execPath).ToList();

        await Assert.That(candidates.Count).IsEqualTo(1);
        await Assert.That(candidates[0]).IsEqualTo("claude");
    }

    [Test]
    public async Task Candidates_does_not_climb_past_a_bare_versions_root() {
        var candidates = AgentProcessNames.Candidates("/versions/2.1.272").ToList();

        await Assert.That(candidates.Count).IsEqualTo(1);
        await Assert.That(candidates[0]).IsEqualTo("2.1.272");
    }

    /// <summary>One runtime hosts every chat in VS Code, so it stays out of what a name lookup for the
    /// <c>copilot</c> vendor finds: that lookup also claims a process for one session.</summary>
    [Test]
    [Arguments("copilot-runtime")]
    [Arguments("copilot-runtime.exe")]
    public async Task The_shared_runtime_is_not_the_copilot_vendors_process(string name) =>
        await Assert.That(AgentProcessNames.Matches(name, "copilot")).IsFalse();

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("/")]
    public async Task Candidates_is_empty_for_an_unusable_path(string? execPath) {
        await Assert.That(AgentProcessNames.Candidates(execPath)).IsEmpty();
    }
}
