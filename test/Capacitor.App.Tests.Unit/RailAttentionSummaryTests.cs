using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class RailAttentionSummaryTests {
    [Test]
    [Arguments(2, 1, "2 sessions need attention (1 failed)")]
    [Arguments(1, 1, "1 session failed")]
    [Arguments(2, 2, "2 sessions failed")]
    public async Task A_failure_dominates_the_mark_without_hiding_the_footer_alert(int count, int failed, string text) {
        var summary = RailAttentionSummary.Create(count, failed, new RailAlert("Sign-in expired", StatusColors.InProgress), null, null);

        await Assert.That(summary.SessionCount).IsEqualTo(count);
        await Assert.That(summary.Text).IsEqualTo(text + " · Sign-in expired");
        await Assert.That(summary.Tone).IsEqualTo(StatusColors.Disrupted);
        await Assert.That(summary.HasSessionCount).IsTrue();
    }

    [Test]
    public async Task An_alert_without_sessions_keeps_its_existing_tone_and_a_dot() {
        var summary = RailAttentionSummary.Create(0, 0, new RailAlert("Reconnecting", StatusColors.Pending), null, null);

        await Assert.That(summary.HasAttention).IsTrue();
        await Assert.That(summary.HasSessionCount).IsFalse();
        await Assert.That(summary.Text).IsEqualTo("Reconnecting");
        await Assert.That(summary.Tone).IsEqualTo(StatusColors.Pending);
    }

    [Test]
    public async Task A_large_count_stays_compact_while_the_text_retains_the_exact_count() {
        var summary = RailAttentionSummary.Create(105, 0, null, null, null);

        await Assert.That(summary.CountText).IsEqualTo("99+");
        await Assert.That(summary.Text).IsEqualTo("105 sessions need you");
    }
}
