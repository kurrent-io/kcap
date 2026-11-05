using Capacitor.App.Services;
using Capacitor.App.Services.Voice;

namespace Capacitor.App.Tests.Unit.Services.Voice;

public class VoiceCommandParserTests {
    [Test]
    [Arguments("Allow.")]
    [Arguments("approve it")]
    [Arguments("Please allow")]
    public async Task Allow_answers_the_permission_request(string spoken) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.PermissionReply(PermissionAnswer.Allow));

    [Test]
    [Arguments("Always allow")]
    [Arguments("allow always")]
    public async Task Always_allow_is_not_read_as_allow(string spoken) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.PermissionReply(PermissionAnswer.AllowAlways));

    [Test]
    public async Task Deny_answers_the_permission_request() =>
        await Assert.That(VoiceCommandParser.Parse("Don't allow that.")).IsEqualTo(new VoiceCommand.PermissionReply(PermissionAnswer.Deny));

    [Test]
    [Arguments("Stop the agent.")]
    [Arguments("kill this session")]
    public async Task Stopping_the_agent_is_its_own_command(string spoken) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsTypeOf<VoiceCommand.StopAgent>();

    [Test]
    [Arguments("Stop.")]
    [Arguments("Be quiet")]
    [Arguments("stop talking")]
    public async Task Stop_alone_silences_speech_rather_than_the_agent(string spoken) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsTypeOf<VoiceCommand.StopSpeaking>();

    [Test]
    [Arguments("Stop the flaky test from retrying forever")]
    [Arguments("Allow users to reset their password")]
    [Arguments("Open the README and fix the typos in the install section")]
    [Arguments("Why does the terminal flicker on resize?")]
    public async Task A_prompt_that_mentions_a_command_word_goes_to_the_agent(string spoken) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.Prompt(spoken));

    [Test]
    [Arguments("Say stop", "stop")]
    [Arguments("Tell it to allow anonymous reads", "allow anonymous reads")]
    [Arguments("prompt: list sessions", "list sessions")]
    public async Task An_explicit_prompt_prefix_sends_even_a_command_phrase(string spoken, string prompt) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.Prompt(prompt, Explicit: true));

    [Test]
    [Arguments("Start a new session to add retries to the uploader", "add retries to the uploader")]
    [Arguments("New session: Refactor the cache.", "Refactor the cache.")]
    [Arguments("launch an agent that writes the changelog", "writes the changelog")]
    public async Task A_new_session_carries_its_goal(string spoken, string goal) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.NewSession(goal));

    [Test]
    public async Task A_new_session_without_a_goal_asks_for_one() =>
        await Assert.That(VoiceCommandParser.Parse("New session.")).IsEqualTo(new VoiceCommand.NewSession(null));

    [Test]
    [Arguments("Open session login fix", "login fix")]
    [Arguments("switch to the uploader session", "uploader")]
    public async Task Opening_a_session_names_it(string spoken, string query) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.OpenSession(query));

    [Test]
    public async Task Opening_a_session_is_not_read_as_starting_one() =>
        await Assert.That(VoiceCommandParser.Parse("Open session two")).IsEqualTo(new VoiceCommand.OpenSession("two"));

    [Test]
    [Arguments("Show the terminal", VoiceTab.Terminal)]
    [Arguments("open PR", VoiceTab.PullRequest)]
    [Arguments("show artifacts", VoiceTab.Artefacts)]
    [Arguments("chat", VoiceTab.Chat)]
    public async Task Tabs_are_shown_by_name(string spoken, VoiceTab tab) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.ShowTab(tab));

    [Test]
    [Arguments("Read more")]
    [Arguments("continue")]
    [Arguments("Next part.")]
    public async Task Steering_a_long_reply_reads_its_next_part(string spoken) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.ReadMore(spoken));

    [Test]
    [Arguments("Read the tests part", "tests")]
    [Arguments("go to section three", "three")]
    public async Task A_reply_part_is_asked_for_by_name(string spoken, string query) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.ReadPart(query));

    [Test]
    public async Task Read_it_all_reads_the_whole_reply() =>
        await Assert.That(VoiceCommandParser.Parse("Read it all.")).IsTypeOf<VoiceCommand.ReadAll>();

    [Test]
    public async Task Say_that_again_repeats_what_the_app_said_rather_than_prompting() =>
        await Assert.That(VoiceCommandParser.Parse("Say that again")).IsTypeOf<VoiceCommand.Repeat>();

    [Test]
    [Arguments("use repository kcap cli", "kcap cli")]
    [Arguments("Select the repo web app.", "web app")]
    public async Task Repositories_are_chosen_by_name(string spoken, string query) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.SelectRepository(query));

    [Test]
    public async Task Harnesses_are_chosen_by_name() =>
        await Assert.That(VoiceCommandParser.Parse("Use Codex.")).IsEqualTo(new VoiceCommand.SelectHarness("codex"));

    [Test]
    [Arguments("Type add a test for the parser", "add a test for the parser")]
    public async Task Dictation_fills_the_composer(string spoken, string text) =>
        await Assert.That(VoiceCommandParser.Parse(spoken)).IsEqualTo(new VoiceCommand.Dictate(text));

    [Test]
    [Arguments("Yes.", typeof(VoiceCommand.Confirm))]
    [Arguments("never mind", typeof(VoiceCommand.Cancel))]
    [Arguments("Zoom in", typeof(VoiceCommand.Zoom))]
    [Arguments("What can I say?", typeof(VoiceCommand.Help))]
    [Arguments("What's the status?", typeof(VoiceCommand.Status))]
    [Arguments("Stop listening", typeof(VoiceCommand.StopListening))]
    [Arguments("Next session", typeof(VoiceCommand.NextSession))]
    [Arguments("go home", typeof(VoiceCommand.GoHome))]
    [Arguments("List sessions", typeof(VoiceCommand.ListSessions))]
    public async Task Control_phrases_parse_to_their_command(string spoken, Type expected) =>
        await Assert.That(VoiceCommandParser.Parse(spoken).GetType()).IsEqualTo(expected);
}
