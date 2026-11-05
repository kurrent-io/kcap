using System.Text.Json;
using Capacitor.App.Services.Voice;

namespace Capacitor.App.Tests.Unit.Services.Voice;

public class VoiceIntentTests {
    static readonly VoiceContext Screen = new(
        [new("a", "Fix the login redirect loop", "working", true), new("b", "Update the docs site", "waiting for you", false)],
        "Fix the login redirect loop", "kcap-cli with Claude", null, null);

    static string Answer(string action, string value = "", string session = "") =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["action"] = action, ["value"] = value, ["session"] = session });

    [Test]
    public async Task A_named_session_is_opened_before_the_action() {
        var commands = VoiceIntent.Read(Answer("stop_agent", session: "Update the docs site"), "close the docs one", Screen);

        await Assert.That(commands).IsEquivalentTo(new VoiceCommand[] { new VoiceCommand.OpenSession("Update the docs site"), new VoiceCommand.StopAgent() });
    }

    [Test]
    public async Task The_open_session_or_its_listed_status_is_not_opened_again() {
        await Assert.That(VoiceIntent.Read(Answer("show_tab", "terminal", "Fix the login redirect loop (working)"), "show the terminal", Screen))
            .IsEquivalentTo(new VoiceCommand[] { new VoiceCommand.ShowTab(VoiceTab.Terminal) });
    }

    [Test]
    public async Task A_session_named_for_an_app_command_is_ignored() =>
        await Assert.That(VoiceIntent.Read(Answer("zoom_in", session: "Update the docs site"), "bigger", Screen))
            .IsEquivalentTo(new VoiceCommand[] { new VoiceCommand.Zoom(1) });

    [Test]
    public async Task A_prompt_it_chose_is_sent_as_is_and_an_empty_one_keeps_the_words() {
        await Assert.That(VoiceIntent.Read(Answer("prompt", "fix the broken links"), "tell it to fix the broken links", Screen))
            .IsEquivalentTo(new VoiceCommand[] { new VoiceCommand.Prompt("fix the broken links", Explicit: true) });
        await Assert.That(VoiceIntent.Read(Answer("prompt"), "why does it redirect twice", Screen))
            .IsEquivalentTo(new VoiceCommand[] { new VoiceCommand.Prompt("why does it redirect twice", Explicit: true) });
    }

    [Test]
    public async Task An_answer_or_a_question_back_is_said() =>
        await Assert.That(VoiceIntent.Read(Answer("unclear", "Which session, login or docs?"), "stop it", Screen))
            .IsEquivalentTo(new VoiceCommand[] { new VoiceCommand.Say("Which session, login or docs?") });

    [Test]
    [Arguments("{\"action\":\"format_disk\",\"value\":\"\",\"session\":\"\"}")]
    [Arguments("{\"action\":\"open_session\",\"value\":\"\",\"session\":\"\"}")]
    [Arguments("{\"action\":\"answer\",\"value\":\"\",\"session\":\"\"}")]
    [Arguments("[]")]
    [Arguments("not json")]
    public async Task Anything_but_a_complete_known_action_reads_as_no_answer(string answer) =>
        await Assert.That(VoiceIntent.Read(answer, "hello", Screen)).IsNull();

    [Test]
    public async Task The_schema_offers_every_action_the_instructions_describe() {
        using var schema = JsonDocument.Parse(VoiceIntent.Schema);
        var actions = schema.RootElement.GetProperty("properties").GetProperty("action").GetProperty("enum")
            .EnumerateArray().Select(a => a.GetString()!).ToList();

        await Assert.That(actions).Contains("stop_agent");
        foreach (var action in actions) await Assert.That(VoiceIntent.SystemPrompt).Contains($"- {action}: ");
    }

    [Test]
    public async Task The_message_lists_what_is_on_screen() {
        var message = VoiceIntent.Message("close the docs one", Screen);

        await Assert.That(message).Contains("- Fix the login redirect loop (working) [open]");
        await Assert.That(message).Contains("- Update the docs site (waiting for you)");
        await Assert.That(message).EndsWith("The user said: \"close the docs one\"");
    }
}
