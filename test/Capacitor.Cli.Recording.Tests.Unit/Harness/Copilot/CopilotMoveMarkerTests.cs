using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

/// <summary>
/// A chat VS Code moved from Local to Copilot, shaped like a captured one: a migration <c>session.start</c>, the Local
/// chat's prompt at its chat-log request time, then the resume that ends the copied history. The watcher finds the Local
/// chat whose request has that time and text among the chat logs of the workspaces open on the chat's folder, and sends
/// a marker naming it after the resume.
/// </summary>
public class CopilotMoveMarkerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string MovedId = "e66918ea-28db-4c85-879a-f4538ce0725e";
    const string LocalId = "fa0d6e8d-7c9d-446f-a93c-5a2a89e011b4";
    const string Cwd     = "/work/vsc-a";
    const string Prompt  = "Add a multiply(a, b) function to calc.py.";
    const long   SentAt  = 1795024287408;   // 2026-11-18T17:51:27.408Z

    static string[] Transcript(string producer = CopilotMovedChat.Producer) => [
        $$$"""{"type":"session.start","data":{"sessionId":"{{{MovedId}}}","producer":"{{{producer}}}","startTime":"2026-11-18T17:51:27.399Z","context":{"cwd":"{{{Cwd}}}"}},"id":"00000000-0000-4000-8000-000000000001","timestamp":"2026-11-18T17:51:27.398Z"}""",
        $$$"""{"type":"user.message","data":{"content":"{{{Prompt}}}","source":"user"},"id":"00000000-0000-4000-8000-000000000002","timestamp":"2026-11-18T17:51:27.408Z"}""",
        """{"type":"assistant.message","data":{"content":"Added multiply."},"id":"00000000-0000-4000-8000-000000000003","timestamp":"2026-11-18T17:51:27.409Z"}""",
        """{"type":"session.resume","data":{"resumeTime":"2026-11-18T17:53:40.845Z","context":{}},"id":"00000000-0000-4000-8000-000000000004","timestamp":"2026-11-18T17:53:40.853Z"}""",
        """{"type":"user.message","data":{"content":"Add divide."},"id":"00000000-0000-4000-8000-000000000005","timestamp":"2026-11-18T17:53:46.468Z"}""",
    ];

    /// <summary>A workspace in VS Code's storage open on <paramref name="folder"/>, holding a Local chat log whose one
    /// request has the given time and prompt.</summary>
    string Workspace(string name, string folder, string chatId, long sentAt = SentAt, string prompt = Prompt) {
        Tmp.CreateFile(Path.Combine("storage", name, "workspace.json"), new JsonObject { ["folder"] = new Uri(folder).AbsoluteUri }.ToJsonString());

        var request = new JsonObject { ["requestId"] = "request_1", ["timestamp"] = sentAt, ["message"] = new JsonObject { ["text"] = prompt } };
        var log     = new JsonObject { ["kind"] = 0, ["v"] = new JsonObject { ["version"] = 3, ["sessionId"] = chatId, ["requests"] = new JsonArray(request) } };
        Tmp.CreateFile(Path.Combine("storage", name, "chatSessions", chatId + ".jsonl"), log.ToJsonString() + "\n");

        return Tmp.PathTo("storage");
    }

    static string? Find(string storage, string[] transcript) =>
        CopilotMovedChat.Read(transcript) is { } history ? CopilotMovedChat.FindLocalChat([storage], history) : null;

    [Test]
    public async Task A_moved_chats_history_names_its_first_prompt_and_the_resume_that_ends_it() {
        var history = CopilotMovedChat.Read(Transcript());

        await Assert.That(history!.Cwd).IsEqualTo(Cwd);
        await Assert.That((history.FirstPromptAtMs, history.FirstPrompt, history.ResumeLine)).IsEqualTo((SentAt, Prompt, 3));
    }

    [Test]
    public async Task Another_chats_transcript_or_one_whose_history_has_not_ended_has_none() {
        await Assert.That(CopilotMovedChat.Read(Transcript(producer: "copilot-agent"))).IsNull();
        await Assert.That(CopilotMovedChat.Read(Transcript()[..3])).IsNull();
    }

    [Test]
    public async Task The_local_chat_with_the_prompts_time_and_text_is_found() =>
        await Assert.That(Find(Workspace("a", Cwd, LocalId), Transcript())).IsEqualTo(LocalId);

    [Test]
    [Arguments("time")]
    [Arguments("text")]
    [Arguments("folder")]
    [Arguments("two")]
    public async Task No_local_chat_is_named_when_none_or_several_match(string change) {
        var storage = change switch {
            "time"   => Workspace("a", Cwd, LocalId, sentAt: SentAt + 1),
            "text"   => Workspace("a", Cwd, LocalId, prompt: Prompt + "!"),
            "folder" => Workspace("a", "/work/other", LocalId),
            _        => TwoMatching(),
        };

        await Assert.That(Find(storage, Transcript())).IsNull();

        string TwoMatching() {
            Workspace("b", Cwd, "11111111-1111-4111-8111-111111111111");
            return Workspace("a", Cwd, LocalId);
        }
    }

    [Test]
    public async Task The_marker_goes_after_the_resume_that_ends_the_history_and_is_staged() {
        var storage = Workspace("a", Cwd, LocalId);
        var path    = Tmp.CreateFile("events.jsonl", Transcript());
        var state   = new WatchState();
        List<string> lines   = [.. Transcript()[2..]];
        List<int>    numbers = [2, 3, 4];

        CopilotMoveMarker.Append(state, lines, numbers, path, MovedId.Replace("-", ""), [storage], _ => { });

        await Assert.That(numbers).IsEquivalentTo(new[] { 2, 3, CopilotMovedChat.MarkerLineNumber, 4 });
        await Assert.That(JsonNode.Parse(lines[2])!["data"]!["sessionId"]!.GetValue<string>()).IsEqualTo(LocalId);
        await Assert.That(state.CopilotMoveMarkerStaged).IsTrue();
    }

    // A later drain carries a later resume, which ends nothing.
    [Test]
    public async Task A_drain_without_the_ending_resume_stages_nothing() {
        var storage = Workspace("a", Cwd, LocalId);
        var path    = Tmp.CreateFile("events.jsonl", [.. Transcript(), Transcript()[3]]);
        var state   = new WatchState { CopilotMoveMarkerStaged = true };
        List<string> lines   = [Transcript()[3]];
        List<int>    numbers = [5];

        CopilotMoveMarker.Append(state, lines, numbers, path, MovedId.Replace("-", ""), [storage], _ => { });

        await Assert.That(numbers).IsEquivalentTo(new[] { 5 });
        await Assert.That(state.CopilotMoveMarkerStaged).IsFalse();
    }
}
