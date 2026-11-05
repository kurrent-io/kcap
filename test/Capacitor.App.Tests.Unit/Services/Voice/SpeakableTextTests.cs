using Capacitor.App.Services.Voice;

namespace Capacitor.App.Tests.Unit.Services.Voice;

public class SpeakableTextTests {
    [Test]
    public async Task Code_blocks_are_announced_not_read() {
        var spoken = SpeakableText.From("Here is the fix:\n\n```csharp\nvar x = 1;\n```\n\nRun the tests.");
        await Assert.That(spoken).IsEqualTo("Here is the fix: (code block on screen). Run the tests.");
    }

    [Test]
    public async Task Markdown_marks_and_link_targets_are_dropped() {
        var spoken = SpeakableText.From("## Summary\n\n- **Fixed** the [login page](https://example.com/login)\n- Added `RetryPolicy`");
        await Assert.That(spoken).IsEqualTo("Summary. Fixed the login page. Added RetryPolicy");
    }

    [Test]
    public async Task A_long_reply_stops_at_a_sentence_and_points_at_the_screen() {
        var reply = string.Join(" ", Enumerable.Repeat("This sentence is about forty characters.", 40));
        var spoken = SpeakableText.From(reply, limit: 200);

        await Assert.That(spoken.Length).IsLessThan(240);
        await Assert.That(spoken).EndsWith("characters. The rest is on screen.");
    }
}
