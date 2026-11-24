using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Pins when setup asks about Grok Bot, and that it asks at most once per profile.</summary>
public class GrokBotSetupStepTests {
    readonly List<string> _lines = [];
    int _asked;

    bool? Run(bool detected = true, bool? recorded = null, bool noPrompt = false, bool interactive = true, Func<string, bool>? answer = null) =>
        GrokBotSetupStep.Run(detected, recorded, noPrompt, interactive, text => { _asked++; return (answer ?? (_ => true))(text); }, _lines.Add);

    [Test]
    public async Task Without_the_desktop_app_nothing_is_asked_or_said() {
        await Assert.That(Run(detected: false)).IsNull();
        await Assert.That(_asked).IsEqualTo(0);
        await Assert.That(_lines).IsEmpty();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_unasked_profile_is_asked_after_being_told_the_scope_and_its_answer_is_kept(bool answer) {
        await Assert.That(Run(answer: _ => answer)).IsEqualTo(answer);
        await Assert.That(_asked).IsEqualTo(1);
        await Assert.That(string.Join("\n", _lines)).Contains("private to you");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_answered_profile_is_not_asked_again(bool recorded) {
        await Assert.That(Run(recorded: recorded)).IsNull();
        await Assert.That(_asked).IsEqualTo(0);
        await Assert.That(_lines.Count).IsEqualTo(recorded ? 1 : 0);
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(false, false)]
    public async Task With_nobody_to_ask_it_says_how_to_turn_recording_on_and_stores_nothing(bool noPrompt, bool interactive) {
        await Assert.That(Run(noPrompt: noPrompt, interactive: interactive)).IsNull();
        await Assert.That(_asked).IsEqualTo(0);
        await Assert.That(_lines).IsEquivalentTo([GrokBotSetupStep.Hint]);
    }

    [Test]
    public async Task A_prompt_that_cannot_be_shown_falls_back_to_the_hint() {
        await Assert.That(Run(answer: _ => throw new InvalidOperationException("no terminal"))).IsNull();
        await Assert.That(_lines[^1]).IsEqualTo(GrokBotSetupStep.Hint);
    }
}
