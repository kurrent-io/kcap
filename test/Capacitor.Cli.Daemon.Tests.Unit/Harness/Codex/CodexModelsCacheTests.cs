using System.Text;
using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

public class CodexModelsCacheTests {
    [Test]
    [Arguments("""{"client_version":"0.169.0","models":[]}""", "0.169.0", true)]
    [Arguments("""{"client_version":"0.170.0","models":[]}""", "0.169.0", false)]
    [Arguments("""{"client_version":"0.170.0","models":[]}""", null, true)]
    [Arguments("""{"models":[]}""", "0.169.0", true)]
    [Arguments("""{"client_version":169,"models":[]}""", "0.169.0", true)]
    [Arguments("""not json""", "0.169.0", true)]
    public async Task WrittenBy_compares_the_stamp_only_when_both_sides_have_one(string content, string? version, bool expected) =>
        await Assert.That(CodexModelsCache.WrittenBy(Encoding.UTF8.GetBytes(content), version)).IsEqualTo(expected);
}
