using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Antigravity;

namespace Capacitor.Cli.Tests.Unit.Harness.Antigravity;

/// <summary>Discovery prints into the setup wizard and <c>kcap import --discover</c>, so it reports format drift
/// as one plain warning and says nothing about a brain tree that has none.</summary>
public class AntigravityImportSourceDiscoveryTests {
    const string Conversation = "aaaaaaaa-0000-0000-0000-00000000a001";

    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine =
        """{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2026-07-02T19:00:00Z","content":"<USER_REQUEST>hi</USER_REQUEST>"}""";

    async Task<(int Discovered, string Stderr)> Discover() {
        var source = new AntigravityImportSource(new(new(Tmp.Path), ""), TimeProvider.System);
        using var stderr = new StringWriter();

        var discovered = await source.DiscoverAsync(
            new DiscoveryFilters(FilterCwd: null, FilterSession: null, Since: null, MinLines: 0) { Stderr = stderr },
            CancellationToken.None);

        return (discovered.Count, stderr.ToString());
    }

    void WriteConversation() =>
        Tmp.CreateFile([".gemini", "antigravity", "brain", Conversation, ".system_generated", "logs", "transcript_full.jsonl"], UserLine + "\n");

    [Test]
    public async Task A_brain_tree_without_drift_writes_nothing() {
        WriteConversation();

        var (discovered, stderr) = await Discover();

        await Assert.That(discovered).IsEqualTo(1);
        await Assert.That(stderr).IsEmpty();
    }

    [Test]
    public async Task Messages_without_an_invoke_edge_write_one_warning() {
        WriteConversation();
        Tmp.CreateDir(".gemini", "antigravity", "brain", Conversation, ".system_generated", "messages");

        var (_, stderr) = await Discover();
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).StartsWith("! Antigravity: 1 conversation(s) have messages but no invoke edge");
    }
}
