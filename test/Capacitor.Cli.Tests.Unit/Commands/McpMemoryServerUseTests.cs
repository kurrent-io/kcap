using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>get_memory counts its fetch with a POST of its own, and marks its GET so the server does not count the same
/// fetch there too: for the id the server answered with, never the slug asked for, and never at the cost of the memory
/// it fetched.</summary>
public class McpMemoryServerUseTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string MemoryId = "0123456789abcdef0123456789abcdef";

    const string Memory = $$"""{"memory_id":"{{MemoryId}}","slug":"hnsw-dimensions","content":"Retype the column first."}""";

    McpMemoryServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient(), NoTelemetry.Startup,
            new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory), TimeProvider.System);

    async Task<(string Text, bool IsError, string[] Requests)> GetMemoryAsync(int getStatus, string getBody, int useStatus, TimeSpan? useDelay = null) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingGet())
            .RespondWith(Response.Create().WithStatusCode(getStatus).WithHeader("Content-Type", "application/json").WithBody(getBody));
        var use = Response.Create().WithStatusCode(useStatus).WithHeader("Content-Type", "application/json").WithBody("{}");
        server.Given(Request.Create().UsingPost()).RespondWith(useDelay is { } delay ? use.WithDelay(delay) : use);

        using var client = new HttpClient();
        var request = new JsonObject {
            ["params"] = new JsonObject { ["name"] = "get_memory", ["arguments"] = JsonNode.Parse("""{"id_or_slug":"hnsw-dimensions"}""") }
        };

        var response = await Server().HandleToolCallAsync(JsonNode.Parse("1")!, request, client, server.Url!, "repo-hash", "mach-1");
        var result   = JsonNode.Parse(response)!["result"]!;

        return (
            result["content"]![0]!["text"]!.GetValue<string>(),
            result["isError"]?.GetValue<bool>() ?? false,
            [.. server.LogEntries.Select(e => $"{e.RequestMessage.Method} {e.RequestMessage.Path}{(e.RequestMessage.Headers?.TryGetValue("X-Kcap-Use", out var use) == true ? $" X-Kcap-Use: {string.Join(',', use)}" : "")}")]
        );
    }

    [Test]
    [Arguments(200)]
    [Arguments(404)]
    [Arguments(405)]
    public async Task A_fetched_memory_posts_one_use_for_its_id_and_is_returned_whatever_the_use_answers(int useStatus) {
        var (text, isError, requests) = await GetMemoryAsync(200, Memory, useStatus);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo(Memory);
        await Assert.That(requests).IsEquivalentTo(
            ["GET /api/memories/hnsw-dimensions X-Kcap-Use: posted", $"POST /api/memories/{MemoryId}/uses"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>A use POST that stalls never holds the memory it follows past its own short deadline.</summary>
    [Test]
    public async Task A_stalled_use_does_not_hold_the_fetched_memory() {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var (text, isError, _) = await GetMemoryAsync(200, Memory, 200, useDelay: TimeSpan.FromSeconds(30));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo(Memory);
        await Assert.That(started.Elapsed).IsLessThan(McpMemoryServer.UseDeadline + TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task A_memory_that_was_not_fetched_posts_no_use() {
        var (_, isError, requests) = await GetMemoryAsync(404, "", 200);

        await Assert.That(isError).IsTrue();
        await Assert.That(requests).IsEquivalentTo(["GET /api/memories/hnsw-dimensions X-Kcap-Use: posted"]);
    }
}
