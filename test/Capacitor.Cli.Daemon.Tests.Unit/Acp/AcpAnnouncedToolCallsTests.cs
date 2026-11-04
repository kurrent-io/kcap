using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>Frames as vibe-acp 3.0.1 sends them: the call is announced with its title, kind and input, and the
/// permission request that follows names it by id alone.</summary>
public class AcpAnnouncedToolCallsTests {
    const string CallId = "effect-e224caf5-aae7-5cca-8658-de064000b6f9";

    static JsonElement Json(string json) {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    static JsonElement Announcement(string sessionUpdate = "tool_call") => Json($$$"""
        {"sessionUpdate":"{{{sessionUpdate}}}","toolCallId":"{{{CallId}}}","title":"bash: curl https://example.com",
         "kind":"execute","status":"in_progress","rawInput":{"command":"curl https://example.com"}}
        """);

    static AcpRequest Permission(string toolCall) => new(7, "session/request_permission", Json($$"""
        {"sessionId":"16cb0a52-4d43-3d08-6f37-683d33bc2424","toolCall":{{toolCall}},
         "options":[{"optionId":"allow_once","name":"Allow once","kind":"allow_once"}]}
        """));

    [Test]
    public async Task A_bare_permission_request_gets_the_announced_title_kind_and_input() {
        var calls = new AcpAnnouncedToolCalls();
        calls.Observe(Announcement());

        var completed = calls.Complete(Permission($$"""{"toolCallId":"{{CallId}}"}"""));
        var toolCall  = completed.Params!.Value.GetProperty("toolCall");

        await Assert.That(toolCall.GetProperty("title").GetString()).IsEqualTo("bash: curl https://example.com");
        await Assert.That(toolCall.GetProperty("kind").GetString()).IsEqualTo("execute");
        await Assert.That(toolCall.GetProperty("rawInput").GetProperty("command").GetString()).IsEqualTo("curl https://example.com");
        await Assert.That(completed.Params!.Value.GetProperty("options").GetArrayLength()).IsEqualTo(1);
        await Assert.That(completed.Id).IsEqualTo(7);
    }

    /// <summary>The request is the vendor's statement of what it asks; the announcement only fills its gaps.</summary>
    [Test]
    public async Task A_field_the_request_carries_is_kept() {
        var calls = new AcpAnnouncedToolCalls();
        calls.Observe(Announcement());

        var completed = calls.Complete(Permission($$"""{"toolCallId":"{{CallId}}","kind":"edit"}"""));

        await Assert.That(completed.Params!.Value.GetProperty("toolCall").GetProperty("kind").GetString()).IsEqualTo("edit");
    }

    [Test]
    public async Task A_later_update_wins() {
        var calls = new AcpAnnouncedToolCalls();
        calls.Observe(Announcement());
        calls.Observe(Json($$"""{"sessionUpdate":"tool_call_update","toolCallId":"{{CallId}}","title":"bash: curl -I https://example.com"}"""));

        var completed = calls.Complete(Permission($$"""{"toolCallId":"{{CallId}}"}"""));

        await Assert.That(completed.Params!.Value.GetProperty("toolCall").GetProperty("title").GetString()).IsEqualTo("bash: curl -I https://example.com");
    }

    [Test]
    public async Task A_call_never_announced_leaves_the_request_as_it_came() {
        var calls   = new AcpAnnouncedToolCalls();
        var request = Permission("""{"toolCallId":"effect-unknown"}""");

        await Assert.That(calls.Complete(request)).IsSameReferenceAs(request);
    }

    [Test]
    public async Task Another_method_is_left_alone() {
        var calls = new AcpAnnouncedToolCalls();
        calls.Observe(Announcement());
        var request = new AcpRequest(3, "elicitation/create", Json($$$"""{"toolCall":{"toolCallId":"{{{CallId}}}"}}"""));

        await Assert.That(calls.Complete(request)).IsSameReferenceAs(request);
    }

    [Test]
    public async Task The_oldest_call_is_forgotten_past_capacity() {
        var calls = new AcpAnnouncedToolCalls();
        calls.Observe(Announcement());

        for (var i = 0; i < 256; i++)
            calls.Observe(Json($$"""{"sessionUpdate":"tool_call","toolCallId":"call-{{i}}","title":"t"}"""));

        var request = Permission($$"""{"toolCallId":"{{CallId}}"}""");

        await Assert.That(calls.Complete(request)).IsSameReferenceAs(request);
    }
}
