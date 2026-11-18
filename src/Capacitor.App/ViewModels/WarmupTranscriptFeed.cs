using Capacitor.Cli.Core;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.App.ViewModels;

/// A made-up transcript with one of each common row, a lone tool call and a group among them,
/// read through the same parse and projection a stored session takes, so a chat built from it
/// exercises the code a real first open runs.
internal sealed class WarmupTranscriptFeed : IChatTranscriptFeed {
    const string Vendor = "claude";

    static readonly (string Type, string Json)[] Events = [
        (CanonicalEventTypes.UserMessageReceived, """{"content":"Why does the build fail on the `release` branch?"}"""),
        (CanonicalEventTypes.AssistantThinkingGenerated, """{"content":"Check the build script first."}"""),
        (CanonicalEventTypes.AssistantToolCallsGenerated,
            """{"tool_calls":[{"call_id":"w1","tool_name":"Bash","arguments":{"command":"git status --short","description":"Show changes"}}]}"""),
        (CanonicalEventTypes.ToolResultReceived, """{"call_id":"w1","result":" M build.sh\n?? out/"}"""),
        (CanonicalEventTypes.AssistantTextGenerated, """{"content":"The script is modified."}"""),
        (CanonicalEventTypes.AssistantToolCallsGenerated,
            """{"tool_calls":[{"call_id":"w2","tool_name":"Read","arguments":{"file_path":"/repo/build.sh"}},{"call_id":"w3","tool_name":"Edit","arguments":{"file_path":"/repo/build.sh","old_string":"set -e","new_string":"set -euo pipefail"}}]}"""),
        (CanonicalEventTypes.ToolResultReceived, """{"call_id":"w2","result":"1\t#!/bin/sh\n2\tset -e"}"""),
        (CanonicalEventTypes.ToolResultReceived, """{"call_id":"w3","result":"The file /repo/build.sh has been updated."}"""),
        (CanonicalEventTypes.AssistantTextGenerated, """
            {"content":"## Cause\n\nThe script exits on the **first** unset variable. Run:\n\n```bash\nset -euo pipefail\n./build.sh --release\n```\n\n- one\n- two\n\n| Step | Result |\n|---|---|\n| build | ok |\n\nSee [the docs](https://example.com) and `build.sh`."}
            """),
    ];

    bool _read;

    public long? CurrentOffset => null;

    public FeedRead ReadAppended() {
        if (_read) return new(FeedStatus.Ok, []);
        _read = true;
        var rules = TranscriptChat.RulesFor(Vendor);
        var scheduled = new ScheduledPrompts();
        var at = DateTimeOffset.UnixEpoch;
        var lines = new List<ProjectedLine>();
        for (var i = 0; i < Events.Length; i++) {
            if (CanonicalEventJson.TryParse(Events[i].Type, Events[i].Json) is not { } payload) continue;
            var projected = TranscriptChat.Project(new CanonicalEvent(Events[i].Type, payload, Guid.Empty, at.AddSeconds(i)), rules, scheduled);
            lines.Add(new(projected, i));
        }
        return new(FeedStatus.Reset, lines, Events.Length);
    }

    public void Dispose() { }

    internal static ChatSessionInfo Session(string feedKey) =>
        new("Exited", "Finished", Vendor, null, null, null, true, "", feedKey);
}
