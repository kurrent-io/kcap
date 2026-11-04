using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Pi;

/// <summary>Fixtures are <c>pi -p --mode json</c> output captured from real runs, identifiers replaced.</summary>
public class PiJsonEventsTests {
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "pi-headless", $"{name}.jsonl");

    static PiJsonEvents Fold(IEnumerable<string> lines) {
        var events = new PiJsonEvents();
        foreach (var line in lines) events.Accept(line);

        return events;
    }

    static PiJsonEvents FoldFixture(string name) => Fold(File.ReadLines(Fixture(name)));

    [Test]
    public async Task A_successful_run_gives_the_final_reply_and_usage_summed_over_every_assistant_message() {
        var events = FoldFixture("success");

        await Assert.That(events.StopReason).IsEqualTo("stop");
        await Assert.That(events.ErrorMessage).IsNull();
        await Assert.That(events.Reply!).StartsWith("Now I can analyze this function.");
        await Assert.That(events.Reply!).EndsWith("""{"verdict":"fail","reason":"The loop starts at index 1 instead of 0, causing the first element of the list to be excluded from the sum."}""");

        var usage = events.Usage!;
        await Assert.That(usage with { CostUsd = null }).IsEqualTo(new ClaudeCliUsage(4407, 340, 0, 0, null));
        await Assert.That(usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheWriteTokens).IsEqualTo(4747);
        await Assert.That(Math.Round(usage.CostUsd!.Value, 4)).IsEqualTo(0.0061);
    }

    [Test]
    [Arguments("unauthorised", "401 ")]
    [Arguments("credit-low", "400 ")]
    [Arguments("custom-provider-broken", "OAuth refresh failed for anthropic")]
    public async Task A_failed_run_gives_the_error_and_no_reply(string fixture, string errorPrefix) {
        var events = FoldFixture(fixture);

        await Assert.That(events.StopReason).IsEqualTo("error");
        await Assert.That(events.ErrorMessage!).StartsWith(errorPrefix);
        await Assert.That(events.Reply).IsNull();
        await Assert.That(events.Usage).IsEqualTo(new ClaudeCliUsage(0, 0, 0, 0, 0));
    }

    /// <summary>A run killed before <c>agent_end</c> keeps what its finished assistant messages reported.</summary>
    [Test]
    public async Task Before_agent_end_the_usage_is_what_finished_assistant_messages_reported() {
        var events = Fold(File.ReadLines(Fixture("success")).TakeWhile(l => !l.Contains("\"type\":\"agent_end\"")).Where(l => !l.Contains("\"stopReason\":\"stop\"")));

        await Assert.That(events.Usage with { CostUsd = null }).IsEqualTo(new ClaudeCliUsage(2193, 142, 0, 0, null));
        await Assert.That(events.StopReason).IsNull();
        await Assert.That(events.Reply).IsNull();
    }

    [Test]
    public async Task Lines_that_are_not_events_are_ignored() {
        var events = Fold(["", "not json", "[1,2]", """{"type":"agent_end","messages":"nope"}"""]);

        await Assert.That(events.Usage).IsNull();
        await Assert.That(events.StopReason).IsNull();
        await Assert.That(events.Reply).IsNull();
    }
}
