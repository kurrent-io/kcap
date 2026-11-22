using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Round-trip / wire-shape tests for the ACP permission + elicitation DTOs — proves
/// the source-gen <see cref="CapacitorJsonContext"/> registrations exist and serialize with the
/// exact camelCase wire vocabulary (<see cref="PermissionOutcomeDto"/>'s <c>"selected"</c> /
/// <c>"cancelled"</c> spellings, distinct from the server-internal <c>"cancel"</c>) and snake_case
/// server-contract-mirror field names required for daemon&lt;-&gt;server lockstep.
/// </summary>
public class AcpInteractionMessagesTests {
    [Test]
    public async Task SessionRequestPermissionParams_round_trips_with_camelCase_wire_shape() {
        var toolCall = JsonDocument.Parse("""{"name":"bash","input":{"command":"ls"}}""").RootElement.Clone();
        var src = new SessionRequestPermissionParams(
            SessionId: "sess-1",
            ToolCall:  toolCall,
            Options: [
                new PermissionOptionDto("opt-allow", "Allow", "allow_once"),
                new PermissionOptionDto("opt-deny",  "Deny",  "reject_once")
            ]
        );

        var json = JsonSerializer.Serialize(src, CapacitorJsonContext.Default.SessionRequestPermissionParams);

        JsonObject expected = new() {
            ["sessionId"] = "sess-1",
            ["toolCall"]  = new JsonObject { ["name"] = "bash" },
            ["options"]   = new JsonArray(
                new JsonObject { ["optionId"] = "opt-allow", ["name"] = "Allow", ["kind"] = "allow_once" },
                new JsonObject { ["optionId"] = "opt-deny",  ["name"] = "Deny",  ["kind"] = "reject_once" })
        };
        await Assert.That(WireJson.Pick(json, expected)).IsEqualTo(expected.ToJsonString());

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.SessionRequestPermissionParams)!;
        await Assert.That(back.SessionId).IsEqualTo("sess-1");
        await Assert.That(back.Options[0].OptionId).IsEqualTo("opt-allow");
        await Assert.That(back.Options[1].Kind).IsEqualTo("reject_once");
        await Assert.That(back.ToolCall.GetProperty("name").GetString()).IsEqualTo("bash");
    }

    [Test]
    public async Task PermissionOutcomeDto_selected_serializes_optionId_and_omits_it_when_cancelled() {
        var selected = new PermissionOutcomeDto("selected", "opt-allow");
        var selectedJson = JsonSerializer.Serialize(selected, CapacitorJsonContext.Default.PermissionOutcomeDto);
        JsonObject selectedExpected = new() { ["outcome"] = "selected", ["optionId"] = "opt-allow" };
        await Assert.That(WireJson.Pick(selectedJson, selectedExpected)).IsEqualTo(selectedExpected.ToJsonString());

        // Fail-safe cancellation vocabulary: the ACP wire spelling is "cancelled" (double-L),
        // distinct from the server's internal InterruptOutcomes.Cancel = "cancel".
        var cancelled = new PermissionOutcomeDto("cancelled");
        var cancelledJson = JsonSerializer.Serialize(cancelled, CapacitorJsonContext.Default.PermissionOutcomeDto);
        JsonObject cancelledExpected = new() { ["outcome"] = "cancelled", ["optionId"] = WireJson.Missing };
        await Assert.That(WireJson.Pick(cancelledJson, cancelledExpected)).IsEqualTo(cancelledExpected.ToJsonString());

        var back = JsonSerializer.Deserialize(cancelledJson, CapacitorJsonContext.Default.PermissionOutcomeDto)!;
        await Assert.That(back.Outcome).IsEqualTo("cancelled");
        await Assert.That(back.OptionId).IsNull();
    }

    [Test]
    public async Task PermissionOutcomeResult_round_trips_wrapping_outcome() {
        var src  = new PermissionOutcomeResult(new PermissionOutcomeDto("selected", "opt-1"));
        var json = JsonSerializer.Serialize(src, CapacitorJsonContext.Default.PermissionOutcomeResult);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.PermissionOutcomeResult)!;

        await Assert.That(back.Outcome.Outcome).IsEqualTo("selected");
        await Assert.That(back.Outcome.OptionId).IsEqualTo("opt-1");
    }

    [Test]
    public async Task ElicitationCreateParams_parses_stabilized_form_frame_and_tolerates_unknown_members() {
        // Stabilized wire shape: mode + requestedSchema; no draft-era "options" member exists.
        const string json = """{"sessionId":"sess-1","message":"Pick one","mode":"form","requestedSchema":{"type":"object","properties":{"choice":{"type":"string","enum":["a"]}}},"_meta":{"vendor":true},"novelMember":1}""";
        var back          = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.ElicitationCreateParams)!;

        await Assert.That(back.SessionId).IsEqualTo("sess-1");
        await Assert.That(back.Message).IsEqualTo("Pick one");
        await Assert.That(back.Mode).IsEqualTo("form");
        await Assert.That(back.RequestedSchema!.Value.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(back.RequestId).IsNull();
        await Assert.That(back.ElicitationId).IsNull();
        await Assert.That(back.Url).IsNull();
    }

    [Test]
    public async Task ElicitationCreateParams_tolerates_json_null_message_and_request_id() {
        // The wire can legally carry JSON null for nullable members and a request-scoped frame
        // carries requestId — deserialization must not throw; the bridge owns the semantics.
        const string json = """{"requestId":null,"message":null,"mode":"form","requestedSchema":{"type":"object"}}""";
        var back          = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.ElicitationCreateParams)!;

        await Assert.That(back.Message).IsNull();
        await Assert.That(back.SessionId).IsNull();
        // JSON null deserializes into a Null-kind element (or null) — either way "not present".
        await Assert.That(back.RequestId is null || back.RequestId.Value.ValueKind == JsonValueKind.Null).IsTrue();

        const string urlMode = """{"sessionId":"s","message":"Visit","mode":"url","elicitationId":"e1","url":"https://example.com/x"}""";
        var urlBack          = JsonSerializer.Deserialize(urlMode, CapacitorJsonContext.Default.ElicitationCreateParams)!;
        await Assert.That(urlBack.Mode).IsEqualTo("url");
        await Assert.That(urlBack.ElicitationId).IsEqualTo("e1");
        await Assert.That(urlBack.Url).IsEqualTo("https://example.com/x");
    }

    [Test]
    public async Task ElicitationResponse_serializes_stabilized_action_content_shape_exactly() {
        var multi = new ElicitationResponse("accept", new Dictionary<string, JsonElement> {
            ["areas"] = JsonDocument.Parse("""["a","b"]""").RootElement.Clone()
        });
        var multiJson = JsonSerializer.Serialize(multi, CapacitorJsonContext.Default.ElicitationResponse);
        await Assert.That(multiJson).IsEqualTo("""{"action":"accept","content":{"areas":["a","b"]}}""");

        var single = new ElicitationResponse("accept", new Dictionary<string, JsonElement> {
            ["choice"] = JsonDocument.Parse("\"a\"").RootElement.Clone()
        });
        var singleJson = JsonSerializer.Serialize(single, CapacitorJsonContext.Default.ElicitationResponse);
        await Assert.That(singleJson).IsEqualTo("""{"action":"accept","content":{"choice":"a"}}""");

        // cancel carries NO content member at all — not a null one.
        var cancel = new ElicitationResponse("cancel", null);
        var cancelJson = JsonSerializer.Serialize(cancel, CapacitorJsonContext.Default.ElicitationResponse);
        await Assert.That(cancelJson).IsEqualTo("""{"action":"cancel"}""");
    }

    [Test]
    public async Task AcpInteractionDecision_round_trips_selection_lists_and_tolerates_their_absence() {
        var withLists = new AcpInteractionDecision(
            "answered", "a", "Alpha", null, null, null,
            SelectedOptionIds: ["a", "b"], SelectedOptionLabels: ["Alpha", "Beta"]);
        var json = JsonSerializer.Serialize(withLists, CapacitorJsonContext.Default.AcpInteractionDecision);
        JsonObject expected = new() {
            ["selected_option_ids"]    = new JsonArray("a", "b"),
            ["selected_option_labels"] = new JsonArray("Alpha", "Beta")
        };
        await Assert.That(WireJson.Pick(json, expected)).IsEqualTo(expected.ToJsonString());

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpInteractionDecision);
        await Assert.That(back.SelectedOptionIds!).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(back.SelectedOptionId).IsEqualTo("a");

        // Old-server payload: no list members, plus an unknown future member — must not throw,
        // lists come back null (scalar path stays authoritative).
        const string legacy = """{"outcome":"answered","selected_option_id":"a","selected_option_label":"Alpha","unknown_future_member":{"x":1}}""";
        var legacyBack = JsonSerializer.Deserialize(legacy, CapacitorJsonContext.Default.AcpInteractionDecision);
        await Assert.That(legacyBack.SelectedOptionIds).IsNull();
        await Assert.That(legacyBack.SelectedOptionId).IsEqualTo("a");
    }

    [Test]
    public async Task AcpInteractionRequest_round_trips_selection_bounds_and_tolerates_their_absence() {
        var src = new AcpInteractionRequest(
            AgentId: "agent-1", AcpSessionId: "acp-sess-1", Kind: "elicitation",
            ToolName: null, ToolInput: null, ToolCallId: null, Prompt: "Pick",
            Options: [new AcpInteractionOption("a", "Alpha", null)],
            IsMultiSelect: true, RequestedSchema: null,
            MinSelections: 1, MaxSelections: 2);

        var json = JsonSerializer.Serialize(src, CapacitorJsonContext.Default.AcpInteractionRequest);
        JsonObject expected = new() { ["min_selections"] = 1, ["max_selections"] = 2 };
        await Assert.That(WireJson.Pick(json, expected)).IsEqualTo(expected.ToJsonString());

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpInteractionRequest);
        await Assert.That(back.MinSelections).IsEqualTo(1);
        await Assert.That(back.MaxSelections).IsEqualTo(2);

        const string legacy = """{"agent_id":"agent-1","acp_session_id":"s","kind":"elicitation","is_multi_select":false}""";
        var legacyBack = JsonSerializer.Deserialize(legacy, CapacitorJsonContext.Default.AcpInteractionRequest);
        await Assert.That(legacyBack.MinSelections).IsNull();
        await Assert.That(legacyBack.MaxSelections).IsNull();
    }

    [Test]
    public async Task AcpInteractionRequest_round_trips_and_uses_snake_case_server_contract_wire_shape() {
        var toolInput = JsonDocument.Parse("""{"command":"ls"}""").RootElement.Clone();
        var schema    = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        var src = new AcpInteractionRequest(
            AgentId:       "agent-1",
            AcpSessionId:  "acp-sess-1",
            Kind:          "permission",
            ToolName:      "bash",
            ToolInput:     toolInput,
            ToolCallId:    "call-1",
            Prompt:        null,
            Options:       [new AcpInteractionOption("opt-1", "Allow", "Allow once", "allow_once")],
            IsMultiSelect: false,
            RequestedSchema: schema
        );

        var json = JsonSerializer.Serialize(src, CapacitorJsonContext.Default.AcpInteractionRequest);

        // Server-contract mirror types use the context's default snake_case naming policy
        // (matching HostedPermissionRequest/PermissionResolution precedent), NOT the explicit
        // camelCase JsonPropertyName vocabulary used by the spec-derived Acp.* wire DTOs above.
        JsonObject expected = new() {
            ["agent_id"]         = "agent-1",
            ["acp_session_id"]   = "acp-sess-1",
            ["tool_name"]        = "bash",
            ["tool_input"]       = new JsonObject { ["command"] = "ls" },
            ["tool_call_id"]     = "call-1",
            ["options"]          = new JsonArray(new JsonObject { ["option_id"] = "opt-1" }),
            ["is_multi_select"]  = false,
            ["requested_schema"] = new JsonObject { ["type"] = "object" }
        };
        await Assert.That(WireJson.Pick(json, expected)).IsEqualTo(expected.ToJsonString());

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpInteractionRequest);
        await Assert.That(back.AgentId).IsEqualTo("agent-1");
        await Assert.That(back.Options![0].OptionId).IsEqualTo("opt-1");
        await Assert.That(back.RequestedSchema!.Value.GetProperty("type").GetString()).IsEqualTo("object");
    }

    [Test]
    public async Task AcpInteractionDecision_and_resolution_round_trip() {
        var updatedInput = JsonDocument.Parse("""{"command":"ls -la"}""").RootElement.Clone();
        var decision = new AcpInteractionDecision(
            Outcome:             "selected",
            SelectedOptionId:    "opt-1",
            SelectedOptionLabel: "Allow",
            SelectedIndex:       0,
            FreeText:            null,
            UpdatedToolInput:    updatedInput
        );
        var resolution = new AcpInteractionResolution("req-1", decision);

        var json = JsonSerializer.Serialize(resolution, CapacitorJsonContext.Default.AcpInteractionResolution);
        JsonObject expected = new() {
            ["request_id"] = "req-1",
            ["decision"]   = new JsonObject {
                ["selected_option_id"]    = "opt-1",
                ["selected_option_label"] = "Allow",
                ["updated_tool_input"]    = new JsonObject { ["command"] = "ls -la" }
            }
        };
        await Assert.That(WireJson.Pick(json, expected)).IsEqualTo(expected.ToJsonString());

        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.AcpInteractionResolution);
        await Assert.That(back.RequestId).IsEqualTo("req-1");
        await Assert.That(back.Decision.SelectedOptionId).IsEqualTo("opt-1");
        await Assert.That(back.Decision.UpdatedToolInput!.Value.GetProperty("command").GetString()).IsEqualTo("ls -la");
    }
}
