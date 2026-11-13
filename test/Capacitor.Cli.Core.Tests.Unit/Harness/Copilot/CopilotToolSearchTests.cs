using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Copilot;

public class CopilotToolSearchTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Turns_it_on_in_a_missing_file() {
        var path = Tmp.PathTo("settings.json");

        await Assert.That(CopilotToolSearch.Enable(path)).IsEqualTo(SettingsEdit.Changed);
        await Assert.That(File.ReadAllText(path)).Contains("\"toolSearch\": true");
    }

    /// <summary>A value the user set, either way, is theirs: a refresh after <c>copilot config toolSearch false</c>
    /// must not turn it back on.</summary>
    [Test]
    [Arguments("""{"toolSearch":false}""")]
    [Arguments("""{"toolSearch":true}""")]
    public async Task Leaves_a_value_the_user_set(string content) {
        var path = Tmp.CreateFile("settings.json", content);

        await Assert.That(CopilotToolSearch.Enable(path)).IsEqualTo(SettingsEdit.Unchanged);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(content);
    }

    [Test]
    public async Task Refuses_a_file_it_cannot_parse() {
        const string content = "// mine\n{\"banner\":\"never\"}";
        var path = Tmp.CreateFile("settings.json", content);

        await Assert.That(CopilotToolSearch.Enable(path)).IsEqualTo(SettingsEdit.Malformed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(content);
    }

    [Test]
    [Arguments("""{"toolSearch":false}""", true)]
    [Arguments("""{"toolSearch":true}""", false)]
    [Arguments("""{"toolSearch":"no"}""", false)]
    [Arguments("{}", false)]
    [Arguments("not json", false)]
    public async Task Reads_off_only_from_an_explicit_false(string content, bool off) {
        var path = Tmp.CreateFile("settings.json", content);

        await Assert.That(CopilotToolSearch.IsTurnedOff(path)).IsEqualTo(off);
    }
}
