using Capacitor.Models.Transcripts.UserMessages;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.UserMessages;

public class ScheduledPromptsTests {
    static AssistantToolCallsGenerated WakeUp(string prompt) {
        var call = new AssistantToolCallsGenerated();
        call.ToolCalls.Add(new ToolCallInfo {
            CallId = "toolu_" + prompt.Length, ToolName = "ScheduleWakeup",
            Arguments = Struct.Parser.ParseJson($$"""{"delaySeconds":60,"prompt":"{{prompt}}"}"""),
        });
        return call;
    }

    /// A copy carries the prompts, the pending `/compact` and the turned-down checks, and what either one
    /// observes afterwards stays out of the other.
    [Test]
    public async Task A_copy_holds_the_state_and_is_held_apart_from_the_original() {
        var original = new ScheduledPrompts { Declined = new HashSet<string>(StringComparer.Ordinal) };
        original.Observe(WakeUp("check the build"));
        original.Observe(new UserMessageReceived { Content = "/compact" });
        await Assert.That(original.IsWakeUp("a person's words")).IsFalse();

        var copy = original.Copy();
        original.Observe(WakeUp("check the deploy"));
        await Assert.That(original.IsWakeUp("later words")).IsFalse();
        await Assert.That(copy.IsWakeUp("words of the copy's own")).IsFalse();

        await Assert.That(copy.Prompts).IsEquivalentTo(new[] { "check the build" });
        await Assert.That(copy.AwaitsCompactEcho).IsTrue();
        await Assert.That(copy.Declined!).IsEquivalentTo(new[] { "a person's words", "words of the copy's own" });
        await Assert.That(original.Prompts).IsEquivalentTo(new[] { "check the build", "check the deploy" });
        await Assert.That(original.Declined!).IsEquivalentTo(new[] { "a person's words", "later words" });
    }
}
