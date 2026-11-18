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
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Each test's own VS Code storage, so no workspace one test writes is searched by another.
    readonly string _storage = "storage-" + Guid.NewGuid().ToString("N");

    const string MovedId = "e66918ea-28db-4c85-879a-f4538ce0725e";
    const string LocalId = "fa0d6e8d-7c9d-446f-a93c-5a2a89e011b4";
    const string Prompt  = "Add a multiply(a, b) function to calc.py.";
    const long   SentAt  = 1795024287408;   // 2026-11-18T17:51:27.408Z

    // A real folder, so its file URI is valid on Windows too.
    string Cwd => Tmp.PathTo("work", "vsc-a");

    string[] Transcript(string producer = CopilotMovedChat.Producer, string promptAt = "2026-11-18T17:51:27.408Z") => [
        $$$"""{"type":"session.start","data":{"sessionId":"{{{MovedId}}}","producer":"{{{producer}}}","startTime":"2026-11-18T17:51:27.399Z","context":{"cwd":{{{System.Text.Json.JsonSerializer.Serialize(Cwd)}}}}},"id":"00000000-0000-4000-8000-000000000001","timestamp":"2026-11-18T17:51:27.398Z"}""",
        $$$"""{"type":"user.message","data":{"content":"{{{Prompt}}}","source":"user"},"id":"00000000-0000-4000-8000-000000000002","timestamp":"{{{promptAt}}}"}""",
        """{"type":"assistant.message","data":{"content":"Added multiply."},"id":"00000000-0000-4000-8000-000000000003","timestamp":"2026-11-18T17:51:27.409Z"}""",
        """{"type":"session.resume","data":{"resumeTime":"2026-11-18T17:53:40.845Z","context":{}},"id":"00000000-0000-4000-8000-000000000004","timestamp":"2026-11-18T17:53:40.853Z"}""",
        """{"type":"user.message","data":{"content":"Add divide."},"id":"00000000-0000-4000-8000-000000000005","timestamp":"2026-11-18T17:53:46.468Z","parentId":"00000000-0000-4000-8000-000000000004"}""",
    ];

    /// <summary>A workspace in VS Code's storage open on <paramref name="folder"/>, holding a Local chat log whose one
    /// request has the given time and prompt.</summary>
    string Workspace(string name, string folder, string chatId, long sentAt = SentAt, string prompt = Prompt) {
        Tmp.CreateFile(Path.Combine(_storage, name, "workspace.json"), new JsonObject { ["folder"] = new Uri(folder).AbsoluteUri }.ToJsonString());

        var request = new JsonObject { ["requestId"] = "request_1", ["timestamp"] = sentAt, ["message"] = new JsonObject { ["text"] = prompt } };
        var log     = new JsonObject { ["kind"] = 0, ["v"] = new JsonObject { ["version"] = 3, ["sessionId"] = chatId, ["requests"] = new JsonArray(request) } };
        Tmp.CreateFile(Path.Combine(_storage, name, "chatSessions", chatId + ".jsonl"), log.ToJsonString() + "\n");

        return Tmp.PathTo(_storage);
    }

    static string? Find(string storage, string[] transcript) =>
        CopilotMovedChat.Read(transcript) is { } history ? CopilotMovedChat.FindLocalChat([storage], history).Found : null;

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
            "folder" => Workspace("a", Tmp.PathTo("work", "other"), LocalId),
            _        => TwoMatching(),
        };

        await Assert.That(Find(storage, Transcript())).IsNull();

        string TwoMatching() {
            Workspace("b", Cwd, "11111111-1111-4111-8111-111111111111");
            return Workspace("a", Cwd, LocalId);
        }
    }

    static readonly DateTimeOffset Now = new(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);

    static (List<string> Lines, List<int> Numbers) Append(WatchState state, string path, string storage, string[] drained, int[] numbers, DateTimeOffset? at = null) {
        List<string> lines = [.. drained];
        List<int>    sent  = [.. numbers];
        CopilotMoveMarker.Append(state, lines, sent, path, MovedId.Replace("-", ""), [storage], at ?? Now, _ => { });

        return (lines, sent);
    }

    static string? Named(string line) => JsonNode.Parse(line)!["data"]!["sessionId"]?.GetValue<string>();

    // Numbered as a rewrite renumbers them, so the resume is told by its envelope id, not its file position.
    [Test]
    public async Task The_marker_goes_after_the_resume_that_ends_the_history_and_is_staged() {
        var storage = Workspace("a", Cwd, LocalId);
        var path    = Tmp.CreateFile("events.jsonl", Transcript());
        var state   = new WatchState();

        var (lines, numbers) = Append(state, path, storage, Transcript()[2..], [102, 103, 104]);

        await Assert.That(numbers).IsEquivalentTo(new[] { 102, 103, CopilotMovedChat.MarkerLineNumber, 104 });
        await Assert.That(Named(lines[2])).IsEqualTo(LocalId);
        await Assert.That(state.CopilotMoveMarkerStaged).IsTrue();
    }

    // A watcher restarted past the resume, or one that found the Local chat only later, sends the marker first, even
    // ahead of a line naming the resume as its parent.
    [Test]
    public async Task A_marker_found_after_the_resume_went_out_leads_a_later_drain() {
        var storage = Workspace("a", Cwd, LocalId);
        var path    = Tmp.CreateFile("events.jsonl", Transcript());
        var state   = new WatchState { LinesProcessed = 4 };

        var (lines, numbers) = Append(state, path, storage, [Transcript()[4]], [4]);

        await Assert.That(numbers).IsEquivalentTo(new[] { CopilotMovedChat.MarkerLineNumber, 4 });
        await Assert.That(Named(lines[0])).IsEqualTo(LocalId);
    }

    [Test]
    public async Task A_transcript_of_another_chat_is_read_once_and_gets_no_marker() {
        var storage = Workspace("a", Cwd, LocalId);
        var path    = Tmp.CreateFile("events.jsonl", Transcript(producer: "copilot-agent"));
        var state   = new WatchState();

        var (_, numbers) = Append(state, path, storage, Transcript()[2..], [2, 3, 4]);

        await Assert.That(numbers).IsEquivalentTo(new[] { 2, 3, 4 });
        await Assert.That(state.CopilotTranscriptMoved == false).IsTrue();
    }

    // The Local chat's log can be missing or locked for a while: it is looked for again a minute later, a bounded
    // number of times, and the marker goes out once it is found.
    [Test]
    public async Task A_local_chat_not_found_is_looked_for_again_and_given_up_on_after_the_limit() {
        var path    = Tmp.CreateFile("events.jsonl", Transcript());
        var storage = Tmp.PathTo(_storage);
        var state   = new WatchState { LinesProcessed = 5 };

        await Assert.That(Append(state, path, storage, [], []).Numbers).IsEmpty();
        Workspace("a", Cwd, LocalId);
        await Assert.That(Append(state, path, storage, [], [], Now.AddSeconds(30)).Numbers).IsEmpty();
        await Assert.That(Append(state, path, storage, [], [], Now + CopilotMoveMarker.RetryEvery).Numbers).IsEquivalentTo(new[] { CopilotMovedChat.MarkerLineNumber });

        var hopeless = new WatchState { LinesProcessed = 5 };
        var empty    = Tmp.CreateDir("empty").Path;
        for (var i = 0; i < CopilotMoveMarker.MaxLookups; i++) Append(hopeless, path, empty, [], [], Now + CopilotMoveMarker.RetryEvery * i);

        await Assert.That(hopeless.CopilotMoveMarkerSent).IsTrue();
    }

    [Test]
    public async Task A_sent_marker_is_not_sent_again() {
        var storage = Workspace("a", Cwd, LocalId);
        var path    = Tmp.CreateFile("events.jsonl", Transcript());
        var state   = new WatchState { CopilotMoveMarkerSent = true, CopilotMoveMarkerStaged = true };

        await Assert.That(Append(state, path, storage, Transcript()[2..], [2, 3, 4]).Numbers).IsEquivalentTo(new[] { 2, 3, 4 });
        await Assert.That(state.CopilotMoveMarkerStaged).IsFalse();
    }

    // Only the first copied prompt carries the Local request's time: a later one must never stand in for it.
    [Test]
    public async Task A_history_whose_first_prompt_has_no_time_is_never_matched_and_given_up_on() {
        var storage = Workspace("a", Cwd, LocalId);
        var lines   = Transcript(promptAt: "not a time").ToList();
        lines.Insert(2, $$$"""{"type":"user.message","data":{"content":"{{{Prompt}}}"},"id":"00000000-0000-4000-8000-000000000009","timestamp":"2026-11-18T17:51:27.408Z"}""");
        var path    = Tmp.CreateFile("events.jsonl", [.. lines]);
        var state   = new WatchState();

        await Assert.That(CopilotMovedChat.Read(lines)!.Matchable).IsFalse();
        await Assert.That(Find(storage, [.. lines])).IsNull();
        await Assert.That(Append(state, path, storage, [], []).Numbers).IsEmpty();
        await Assert.That(state.CopilotMoveMarkerSent).IsTrue();
    }

    // A resume the file holds but no drain has sent yet goes out with a later drain, which carries the marker after it.
    [Test]
    public async Task A_resume_not_yet_sent_holds_the_marker_back() {
        var storage = Workspace("a", Cwd, LocalId);
        var path    = Tmp.CreateFile("events.jsonl", Transcript());
        var state   = new WatchState { LinesProcessed = 2 };

        await Assert.That(Append(state, path, storage, [Transcript()[2]], [2]).Numbers).IsEquivalentTo(new[] { 2 });
        await Assert.That(state.CopilotMoveMarkerStaged).IsFalse();
        await Assert.That(state.CopilotMoveLookups).IsEqualTo(0);
    }

    // The marker sits above the server's frontier, so a rewind cannot tell whether the gap took it: it is sent again.
    [Test]
    public async Task A_rewind_sends_the_marker_again() {
        var path  = Tmp.CreateFile("events.jsonl", Transcript());
        var state = new WatchState { LinesProcessed = 5, ThresholdReached = true, CopilotMoveMarkerSent = true, CopilotMoveLookups = 3 };

        var rewound = await new TestRecorders(Config.Root).For(MovedId.Replace("-", ""), path, null, vendor: "copilot")
            .GatedReregisterAsync(new SemaphoreSlim(1, 1), state, _ => Task.FromResult(2), null, CancellationToken.None);

        await Assert.That(rewound).IsTrue();
        await Assert.That(state.LinesProcessed).IsEqualTo(2);
        await Assert.That((state.CopilotMoveMarkerSent, state.CopilotMoveLookups)).IsEqualTo((false, 0));
    }

    // A log still being written reads as nothing yet; it could be the match, or a second one, so nothing is named.
    [Test]
    public async Task A_candidate_log_that_cannot_be_read_yet_leaves_the_lookup_unresolved() {
        var storage = Workspace("a", Cwd, LocalId);
        Tmp.CreateFile(Path.Combine(_storage, "a", "chatSessions", "22222222-2222-4222-8222-222222222222.jsonl"), "{\"kind\":0,\"v\":{\"vers");

        var lookup = CopilotMovedChat.FindLocalChat([storage], CopilotMovedChat.Read(Transcript())!);

        await Assert.That(lookup).IsEqualTo(new CopilotMovedChat.LocalChatLookup(null, Unresolved: true));
    }

    // A path no file system takes, in another workspace's definition, rules that workspace out and no other.
    [Test]
    public async Task A_workspace_naming_an_impossible_folder_is_passed_over() {
        var storage = Workspace("a", Cwd, LocalId);
        var file    = Tmp.CreateFile(Path.Combine("defs", "bad.code-workspace"), "{\"folders\":[{\"path\":\"bad\\u0000path\"}]}");
        Tmp.CreateFile(Path.Combine(_storage, "b", "workspace.json"), new JsonObject { ["workspace"] = new Uri(file).AbsoluteUri }.ToJsonString());

        await Assert.That(Find(storage, Transcript())).IsEqualTo(LocalId);
    }
}
