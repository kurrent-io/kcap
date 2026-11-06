using Capacitor.Cli.Core.Harness.Headless;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Headless;

public class StderrTailTests {
    [Test]
    public async Task Lines_are_joined_and_only_the_last_characters_are_kept() {
        var tail = new StderrTail(maxChars: 8);
        tail.Append("first");
        tail.Append("second");
        tail.Append("third");

        await Assert.That(tail.Text).IsEqualTo("nd\nthird");
    }
}
