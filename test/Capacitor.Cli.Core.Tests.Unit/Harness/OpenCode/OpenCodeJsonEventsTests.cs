using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

/// <summary>Fixtures are <c>opencode run --format json</c> output captured from real runs, identifiers and paths
/// replaced. <c>success</c> was captured with tools on, before the lockdown: it is kept for its two steps, whose
/// usage must be summed and whose first step's text is not the reply.</summary>
public class OpenCodeJsonEventsTests {
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "opencode-headless", $"{name}.jsonl");

    static OpenCodeJsonEvents Fold(IEnumerable<string> lines) {
        var events = new OpenCodeJsonEvents();
        foreach (var line in lines) events.Accept(line);

        return events;
    }

    [Test]
    public async Task A_successful_run_gives_the_last_steps_text_and_usage_summed_over_every_step() {
        var events = Fold(File.ReadLines(Fixture("success")));

        await Assert.That(events.Reply).IsEqualTo("""{"verdict":"fail","reason":"The function starts iterating from index 1 instead of 0, causing it to skip the first element of the list."}""");
        await Assert.That(events.ErrorStatus).IsNull();
        await Assert.That(events.ErrorMessage).IsNull();

        var usage = events.Usage!;
        await Assert.That(usage with { CostUsd = null }).IsEqualTo(new ClaudeCliUsage(8, 183, 8114, 8443, null));
        await Assert.That(usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheWriteTokens).IsEqualTo(16_748);
        await Assert.That(Math.Round(usage.CostUsd!.Value, 4)).IsEqualTo(0.0123);
    }

    [Test]
    public async Task An_error_event_gives_its_status_and_message_and_no_reply() {
        var events = Fold(File.ReadLines(Fixture("unauthorised")));

        await Assert.That(events.ErrorStatus).IsEqualTo(401);
        await Assert.That(events.ErrorMessage).IsEqualTo("invalid x-api-key");
        await Assert.That(events.Reply).IsNull();
        await Assert.That(events.Usage).IsNull();
    }

    [Test]
    public async Task An_error_without_data_is_named_by_its_error_name() {
        var events = Fold(["""{"type":"error","error":{"name":"UnknownError"}}"""]);

        await Assert.That(events.ErrorStatus).IsNull();
        await Assert.That(events.ErrorMessage).IsEqualTo("UnknownError");
    }

    [Test]
    public async Task Reasoning_tokens_count_as_output() {
        var events = Fold(["""{"type":"step_finish","part":{"tokens":{"input":1,"output":2,"reasoning":5,"cache":{"read":0,"write":0}},"cost":0}}"""]);

        await Assert.That(events.Usage).IsEqualTo(new ClaudeCliUsage(1, 7, 0, 0, 0));
    }

    [Test]
    public async Task Text_parts_of_the_last_step_are_joined() {
        var events = Fold([
            """{"type":"step_start","part":{}}""",
            """{"type":"text","part":{"text":"one "}}""",
            """{"type":"text","part":{"text":"two"}}""",
        ]);

        await Assert.That(events.Reply).IsEqualTo("one two");
    }

    [Test]
    public async Task Lines_that_are_not_events_are_ignored() {
        var events = Fold(["", "not json", "[1,2]", """{"type":"text","part":"nope"}""", """{"type":"step_finish","part":{"tokens":"x"}}"""]);

        await Assert.That(events.Reply).IsNull();
        await Assert.That(events.ErrorMessage).IsNull();
        await Assert.That(events.Usage).IsEqualTo(new ClaudeCliUsage(0, 0, 0, 0, null));
    }
}
