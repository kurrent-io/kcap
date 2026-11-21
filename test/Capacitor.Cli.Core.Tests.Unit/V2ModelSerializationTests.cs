using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Tests.Unit;

public class V2ModelSerializationTests {
    [Test]
    public async Task SessionEvalCompletedPayloadV2_round_trips_with_structured_suggestions() {
        var src = new SessionEvalCompletedPayloadV2 {
            EvalRunId    = "run-1",
            JudgeModel   = "claude-opus-4-7",
            OverallScore = 4,
            Summary      = "ok",
            Categories   = [],
            Retrospective = new EvalRetrospectiveV2 {
                OverallSummary = "fine",
                Suggestions    = [
                    new RetrospectiveSuggestion { Text = "Run tests before commit",  Audience = "agent" },
                    new RetrospectiveSuggestion { Text = "Discuss design with team", Audience = "human" }
                ]
            }
        };

        var json = JsonSerializer.Serialize(src, CapacitorJsonContext.Default.SessionEvalCompletedPayloadV2);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.SessionEvalCompletedPayloadV2)!;

        await Assert.That(back.Retrospective!.Suggestions[0].Audience).IsEqualTo("agent");
        await Assert.That(back.Retrospective!.Suggestions[1].Text).IsEqualTo("Discuss design with team");
    }

    [Test]
    public async Task EvalRetrospectiveV2_coerces_null_lists_to_empty() {
        const string json = """{"overall":"x","strengths":null,"issues":null,"suggestions":null}""";
        var parsed = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.EvalRetrospectiveV2)!;

        await Assert.That(parsed.Strengths).IsNotNull();
        await Assert.That(parsed.Issues).IsNotNull();
        await Assert.That(parsed.Suggestions).IsNotNull();
        await Assert.That(parsed.Suggestions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Wire_shape_matches_server_snake_case_keys() {
        // The server's V2 route deserializes via the snake_case naming policy
        // PLUS explicit [JsonPropertyName] decorators on the payload type.
        // This test locks in the wire field names so the CLI cannot
        // accidentally rename a field without breaking the server contract.
        var src = new SessionEvalCompletedPayloadV2 {
            EvalRunId    = "run-1",
            JudgeModel   = "j",
            OverallScore = 4,
            Summary      = "s",
            Categories   = [],
            Retrospective = new EvalRetrospectiveV2 {
                OverallSummary = "x",
                Suggestions    = [new() { Text = "t", Audience = "agent" }]
            }
        };

        var json = JsonSerializer.Serialize(src, CapacitorJsonContext.Default.SessionEvalCompletedPayloadV2);

        JsonObject expected = new() {
            ["eval_run_id"]   = "run-1",
            ["judge_model"]   = "j",
            ["overall_score"] = 4,
            ["retrospective"] = new JsonObject {
                ["overall"]     = "x",
                ["suggestions"] = new JsonArray(new JsonObject { ["text"] = "t", ["audience"] = "agent" })
            }
        };
        await Assert.That(WireJson.Pick(json, expected)).IsEqualTo(expected.ToJsonString());
    }
}
