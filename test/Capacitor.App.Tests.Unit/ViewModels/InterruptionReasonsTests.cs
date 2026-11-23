using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class InterruptionReasonsTests {
    public static IEnumerable<Func<string>> Codes() => InterruptionReasons.KnownCodes.Select(c => (Func<string>)(() => c));

    [Test]
    [MethodDataSource(nameof(Codes))]
    public async Task Every_known_reason_reads_as_a_sentence(string code) {
        var sentence = InterruptionReasons.Describe(code);
        await Assert.That(sentence).IsNotEqualTo(code);
        await Assert.That(sentence!).DoesNotContain("_");
    }

    [Test]
    public async Task An_exit_code_and_a_tombstone_mark_keep_the_sentence() {
        await Assert.That(InterruptionReasons.Describe("tombstone:worktree_missing (exit code 3)"))
            .IsEqualTo("Its worktree no longer exists (exit code 3)");
    }

    [Test]
    public async Task An_unknown_reason_is_shown_as_it_came() {
        await Assert.That(InterruptionReasons.Describe("IOException")).IsEqualTo("IOException");
        await Assert.That(InterruptionReasons.Describe("")).IsNull();
    }
}
