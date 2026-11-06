using System.Text.RegularExpressions;
using Capacitor.Cli.Capture;

namespace Capacitor.Cli.Tests.Unit.Capture;

// An unbounded match keeps a loaded runner from tripping the real regex deadline, which would spend
// the drain's one retry ahead of the stall under test.
public class InlineDrainRedactionTests {
    const string Line = """{"text":"token ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""";

    // The first try has started and has yet to end.
    const int MidFirstTry = 3;

    internal static readonly SecretPatterns Unbounded = SecretRedactor.WatcherPatterns.WithMatchTimeout(Regex.InfiniteMatchTimeout);

    readonly List<RedactionLossReason> _losses = [];

    void ReportLoss(RedactionLossReason reason, int _) => _losses.Add(reason);

    [Test]
    public async Task A_line_a_stall_held_is_redacted_on_its_second_try() {
        var clock     = new StallingRedactionClock();
        var redaction = new InlineDrainRedaction(clock, Unbounded);
        clock.StallOnReading(MidFirstTry, TimeSpan.FromSeconds(1.2));

        var held     = redaction.Encode(Line, ReportLoss);
        var captured = redaction.Retry(Line, ReportLoss);

        await Assert.That(held.Consumed).IsEqualTo(0);
        await Assert.That(captured.Consumed).IsEqualTo(1);
        await Assert.That(captured.Lines[0]).Contains("[REDACTED]");
        await Assert.That(captured.Lines[0]).DoesNotContain("ghp_");
        await Assert.That(_losses.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_drain_retries_only_one_line() {
        var redaction = new InlineDrainRedaction(new StallingRedactionClock(), Unbounded);

        redaction.Retry(Line, ReportLoss);

        await Assert.That(redaction.CanRetry).IsFalse();
    }

    /// <summary>A marker sent under this line's number would make the server drop the line a later import sends.</summary>
    [Test]
    public async Task A_line_that_stays_slow_is_held_rather_than_marked() {
        var redaction = new InlineDrainRedaction(new SwitchableRedactionClock(), Unbounded);

        var first  = redaction.Encode(Line, ReportLoss);
        var second = redaction.Retry(Line, ReportLoss);

        await Assert.That(first.Consumed).IsEqualTo(0);
        await Assert.That(second.Consumed).IsEqualTo(0);
        await Assert.That(second.Lines.Count).IsEqualTo(0);
        await Assert.That(_losses.Count).IsEqualTo(0);
    }
}
