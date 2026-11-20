using System.Text.Json;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Pi;

public class PiCommandsTests {
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Extract_reads_each_command_with_its_description() {
        var response = Parse("""{ "type": "response", "command": "get_commands", "success": true, "data": { "commands": [ { "name": "fix-tests", "description": "Fix failing tests", "source": "prompt" }, { "name": "skill:pdf", "description": " ", "source": "skill" }, { "description": "unnamed" } ] } }""");

        await Assert.That(PiCommands.Extract(response)).IsEquivalentTo([
            new HostedAgentCommand("fix-tests", "Fix failing tests", null),
            new HostedAgentCommand("skill:pdf", null, null),
        ]);
    }

    [Test]
    public async Task Extract_returns_empty_for_a_refused_answer() {
        await Assert.That(PiCommands.Extract(Parse("""{ "type": "response", "command": "get_commands", "success": false, "error": "nope" }"""))).IsEmpty();
    }
}
