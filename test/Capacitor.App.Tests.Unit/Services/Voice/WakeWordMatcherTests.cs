using Capacitor.App.Services.Voice;

namespace Capacitor.App.Tests.Unit.Services.Voice;

public class WakeWordMatcherTests {
    [Test]
    [Arguments("Computer, run the tests.", "computer", "run the tests.")]
    [Arguments("computer run the tests", "computer", "run the tests")]
    [Arguments("Okay, computer. Open settings.", "computer", "Open settings.")]
    [Arguments("Hey kcap, list sessions", "hey kcap", "list sessions")]
    [Arguments("Hey K-Cap, list sessions", "hey kcap", "list sessions")]
    [Arguments("Hey Kay cap list sessions", "hey kcap", "list sessions")]
    [Arguments("KCap, yes.", "KCap", "yes.")]
    [Arguments("K. Cap. Stop the session.", "KCap", "Stop the session.")]
    [Arguments("Jarvis: fix the login bug", "Jarvis", "fix the login bug")]
    [Arguments("Computor, status", "computer", "status")]
    public async Task Strips_the_wake_word_and_keeps_the_command_as_spoken(string transcript, string wakeWord, string command) {
        await Assert.That(WakeWordMatcher.TryMatch(transcript, wakeWord, out var remainder)).IsTrue();
        await Assert.That(remainder).IsEqualTo(command);
    }

    [Test]
    public async Task The_wake_word_alone_leaves_an_empty_command() {
        await Assert.That(WakeWordMatcher.TryMatch("Computer.", "computer", out var remainder)).IsTrue();
        await Assert.That(remainder).IsEqualTo("");
    }

    [Test]
    [Arguments("I think the computer is slow today", "computer")]
    [Arguments("The computer in the other room is slow.", "computer")]
    [Arguments("Run the tests", "computer")]
    [Arguments("cap the limit", "hey kcap")]
    public async Task Speech_that_does_not_open_with_the_wake_word_is_ignored(string transcript, string wakeWord) =>
        await Assert.That(WakeWordMatcher.TryMatch(transcript, wakeWord, out _)).IsFalse();

    [Test]
    [Arguments("Keycap, what's the status?")]
    [Arguments("Kickap, yes.")]
    public async Task A_misspelt_short_wake_word_is_a_near_miss_rather_than_a_match(string transcript) {
        await Assert.That(WakeWordMatcher.TryMatch(transcript, "KCap", out _)).IsFalse();
        await Assert.That(WakeWordMatcher.IsNearMiss(transcript, "KCap")).IsTrue();
    }

    [Test]
    [Arguments("Can you pass the salt")]
    [Arguments("I think the cap is loose")]
    public async Task Unrelated_speech_is_not_a_near_miss(string transcript) =>
        await Assert.That(WakeWordMatcher.IsNearMiss(transcript, "KCap")).IsFalse();

    [Test]
    public async Task A_short_wake_word_must_match_exactly() =>
        await Assert.That(WakeWordMatcher.TryMatch("Hal, status", "max", out _)).IsFalse();

    [Test]
    [Arguments("[BLANK_AUDIO]", "")]
    [Arguments("(music) ", "")]
    [Arguments(" *coughs* computer, status ", "computer, status")]
    [Arguments("...", "")]
    public async Task Clean_drops_non_speech_annotations(string transcript, string expected) =>
        await Assert.That(WakeWordMatcher.Clean(transcript)).IsEqualTo(expected);
}
