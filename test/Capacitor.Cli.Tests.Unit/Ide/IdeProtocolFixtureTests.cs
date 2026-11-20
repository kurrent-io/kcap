using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Cli.Ide;
using Capacitor.Client;

namespace Capacitor.Cli.Tests.Unit.Ide;

/// <summary>Each file under <c>test-fixtures/ide-protocol</c> is one wire message another codebase copies; the
/// .NET types must read it and write back exactly the same JSON.</summary>
public class IdeProtocolFixtureTests {
    static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-fixtures", "ide-protocol", name)))!.AsObject();

    enum Kind { Request, Notification, Result, Error }

    /// <summary>For an error, <paramref name="error"/> is the code and message it must carry, and whether it carries <c>data</c>.</summary>
    static async Task EnvelopeAsync(
            JsonObject message, Kind kind, string? method, bool withParams = true, (int Code, string Message, bool WithData)? error = null) {
        await Assert.That(message["jsonrpc"]!.GetValue<string>()).IsEqualTo("2.0");
        await Assert.That(message.ContainsKey("id")).IsEqualTo(kind != Kind.Notification);
        await Assert.That(message.ContainsKey("method")).IsEqualTo(kind is Kind.Request or Kind.Notification);
        await Assert.That(message.ContainsKey("params")).IsEqualTo(withParams && (kind is Kind.Request or Kind.Notification));
        await Assert.That(message.ContainsKey("result")).IsEqualTo(kind == Kind.Result);
        await Assert.That(message.ContainsKey("error")).IsEqualTo(kind == Kind.Error);
        if (method is not null) await Assert.That(message["method"]!.GetValue<string>()).IsEqualTo(method);
        if (kind == Kind.Error) {
            var (code, text, withData) = error ?? throw new ArgumentException("An error fixture needs its expected error.", nameof(error));
            var body = message["error"]!.AsObject();
            string[] members = withData ? ["code", "message", "data"] : ["code", "message"];
            await Assert.That(body["code"]!.GetValue<int>()).IsEqualTo(code);
            await Assert.That(body["message"]!.GetValue<string>()).IsEqualTo(text);
            await Assert.That(body.Select(p => p.Key)).IsEquivalentTo(members);
        }
    }

    static async Task RoundTripsAsync<T>(
            string name, Kind kind, string? method, Func<JsonObject, JsonNode> select, JsonTypeInfo<T> typeInfo,
            (int Code, string Message, bool WithData)? error = null) where T : class {
        var fixture = Fixture(name);
        var node    = select(fixture);
        var value   = node.Deserialize(typeInfo);

        await Assert.That(value).IsNotNull();
        await Assert.That(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(value!, typeInfo), node)).IsTrue();
        await EnvelopeAsync(fixture, kind, method, error: error);
    }

    static JsonNode Params(JsonObject message) => message["params"]!;

    [Test]
    public Task Initialize_request() => RoundTripsAsync("initialize.request.json", Kind.Request, "initialize", Params, IdeJsonContext.Default.InitializeParams);

    [Test]
    public Task Initialize_result() => RoundTripsAsync("initialize.result.json", Kind.Result, null, m => m["result"]!, IdeJsonContext.Default.InitializeResult);

    [Test]
    public Task Initialize_error_unsupported() =>
        RoundTripsAsync("initialize.error.unsupported.json", Kind.Error, null, m => m["error"]!["data"]!, IdeJsonContext.Default.UnsupportedProtocolData,
            (IdeErrorCodes.UnsupportedProtocol, "unsupported_protocol", true));

    /// <summary>No data, so no type to round-trip: the envelope is the whole message.</summary>
    [Test]
    public Task Error_not_initialized() =>
        EnvelopeAsync(Fixture("error.not_initialized.json"), Kind.Error, null, error: (IdeErrorCodes.NotInitialized, "not_initialized", false));

    /// <inheritdoc cref="Error_not_initialized"/>
    [Test]
    public Task Error_method_not_found() =>
        EnvelopeAsync(Fixture("error.method_not_found.json"), Kind.Error, null, error: (IdeErrorCodes.MethodNotFound, "method_not_found", false));

    [Test]
    public Task Connection_changed() => RoundTripsAsync("connection.changed.json", Kind.Notification, "connection/changed", Params, IdeJsonContext.Default.ConnectionChanged);

    [Test]
    public Task Sessions_subscribe_request() => RoundTripsAsync("sessions.subscribe.request.json", Kind.Request, "sessions/subscribe", Params, IdeJsonContext.Default.SubscribeParams);

    [Test]
    public Task Sessions_snapshot() => RoundTripsAsync("sessions.snapshot.json", Kind.Notification, "sessions/snapshot", Params, IdeJsonContext.Default.SessionsSnapshot);

    [Test]
    public async Task Sessions_snapshot_pending_row_is_what_a_pending_launch_maps_to() {
        var fixture = Fixture("sessions.snapshot.json")["params"]!["rows"]!.AsArray()
            .Single(r => r!["origin"]!.GetValue<string>() == "pending")!;
        var launch = new PendingLaunchDto(
            "0f1e2d3c4b5a69788796a5b4c3d2e1f0", "codex", "/home/dev/src/app", "Update the docs",
            new DateTime(2026, 1, 2, 5, 14, 5, DateTimeKind.Utc), Stage: "spawning");

        var row = SessionRowMapping.From(AgentRow.FromPending(launch, new RepoIdentity("repo:acme/app", "acme/app")));

        await Assert.That(JsonSerializer.SerializeToNode(row, IdeJsonContext.Default.SessionRow)!.ToJsonString()).IsEqualTo(fixture.ToJsonString());
    }

    [Test]
    public Task Sessions_changed() => RoundTripsAsync("sessions.changed.json", Kind.Notification, "sessions/changed", Params, IdeJsonContext.Default.SessionsChanged);

    [Test]
    public Task Sessions_set_scope_request() => RoundTripsAsync("sessions.set_scope.request.json", Kind.Request, "sessions/set_scope", Params, IdeJsonContext.Default.SetScopeParams);

    [Test]
    public Task Sessions_scope_changed() => RoundTripsAsync("sessions.scope_changed.json", Kind.Notification, "sessions/scope_changed", Params, IdeJsonContext.Default.ScopeChanged);

    [Test]
    public Task Sessions_unsubscribe_request() => RoundTripsAsync("sessions.unsubscribe.request.json", Kind.Request, "sessions/unsubscribe", Params, IdeJsonContext.Default.UnsubscribeParams);

    /// <summary>No params, so no type to round-trip: the envelope is the whole message.</summary>
    [Test]
    public Task Connection_retry_request() =>
        EnvelopeAsync(Fixture("connection.retry.request.json"), Kind.Request, "connection/retry", withParams: false);

    [Test]
    public Task Context_subscribe_request() =>
        RoundTripsAsync("context.subscribe.request.json", Kind.Request, "context/subscribe", Params, IdeJsonContext.Default.ContextSubscribeParams);

    [Test]
    public Task Context_refresh_request() =>
        RoundTripsAsync("context.refresh.request.json", Kind.Request, "context/refresh", Params, IdeJsonContext.Default.ContextRefParams);

    [Test]
    public Task Context_unsubscribe_request() =>
        RoundTripsAsync("context.unsubscribe.request.json", Kind.Request, "context/unsubscribe", Params, IdeJsonContext.Default.ContextRefParams);

    [Test]
    public Task Context_snapshot() => RoundTripsAsync("context.snapshot.json", Kind.Notification, "context/snapshot", Params, IdeJsonContext.Default.ContextSnapshot);

    [Test]
    public Task Context_changed() => RoundTripsAsync("context.changed.json", Kind.Notification, "context/changed", Params, IdeJsonContext.Default.ContextChanged);

    [Test]
    public async Task Context_snapshot_sections_are_what_a_ready_read_maps_to() {
        var fixture = Fixture("context.snapshot.json")["params"]!;
        const string itemId = "wi_01J8Z3K4M5N6P7Q8R9S0T1V2W3";
        const string viewerId = "user_01J8Z3VQ4M8R2T6W9Y3B5D7F";
        var primary = new SessionWorkItemAssignmentDto { WorkItemId = itemId, Label = "APP-412 — Retry uploads that time out", IsPrimary = true };
        var item = new WorkItemDto {
            WorkItemId    = itemId,
            Title         = "APP-412",
            EnrichedTitle = "Retry uploads that time out",
            Overview      = "Uploads that hit the gateway timeout are dropped without a trace. Retry them with backoff and report the last failure.",
            Key           = new WorkItemKeyDto { ShortKey = "APP-412", Provider = "tracker", Kind = "issue", Value = "APP-412" },
            State         = new WorkItemStateDto { Kind = "in_flight" },
            Links = [
                new WorkItemLinkDto {
                    Kind = "issue", Provider = "tracker", Value = "APP-388", ShortKey = "APP-388", Url = "https://issues.example.com/APP-388",
                    Title = "Large uploads fail silently", LinkClass = "link",
                },
            ],
            Parts = [
                new WorkItemPartDto { WorkItemId = "wi_part_policy", Title = "Add a retry policy", Ordinal = 0, IsSettled = true },
                new WorkItemPartDto { WorkItemId = "wi_part_report", Title = "Report the last failure", Ordinal = 1 },
                new WorkItemPartDto { WorkItemId = "wi_part_docs", Title = "Document the retry limits", Ordinal = 2 },
            ],
            Contributors = [
                new WorkItemContributorDto { UserId = viewerId, DisplayName = "Viewer Example", LastActivityAt = Utc(2026, 3, 4, 10, 15) },
                new WorkItemContributorDto {
                    UserId = "user_01J8Z3DN5P7R9T2V4X6Z8B1C", DisplayName = "Dana Example", AvatarUrl = "https://avatars.example.com/dana.png",
                    LastActivityAt = Utc(2026, 3, 3, 16, 40),
                },
                new WorkItemContributorDto { UserId = "user_01J8Z3ABCDEFGHJKMNPQRSTV", LastActivityAt = Utc(2026, 3, 2, 9, 5) },
            ],
            SessionCount = 3,
        };
        var topology = new WorkItemTopologyDto {
            PartOf    = new WorkItemRefDto { WorkItemId = "wi_parent", Title = "Reliable uploads" },
            BlockedBy = [new WorkItemRefDto { WorkItemId = "wi_blocker", Title = "Expose the upload queue depth" }],
        };
        var read = new WorkContextRead(
            WorkContextReadKind.Ready, [primary, new SessionWorkItemAssignmentDto { WorkItemId = "wi_part_report", Label = "Report the last failure" }],
            primary, item, topology, new SessionSummaryDto { SessionId = "4f2b9c1e7a3d4e8f9b6c2d1a0e5f3c7b" }, false, false, false, null) {
            Viewer = new ViewerIdentityDto { UserId = viewerId, AvatarUrl = "https://avatars.example.com/viewer.png" },
        };
        var plans = new SessionPlansRead(SessionPlansReadKind.Ready, [
            new SessionPlanDto {
                PlanId = "plan-retry", IsCurrent = true,
                Tasks = [
                    new PlanLedgerTaskDto { TaskId = "t1", Ordinal = 0, Title = "Write the failing retry test", Status = "completed" },
                    new PlanLedgerTaskDto { TaskId = "t2", Ordinal = 1, Title = "Add the retry policy", Note = "Backoff starts at one second", Status = "in_progress" },
                    new PlanLedgerTaskDto { TaskId = "t3", Ordinal = 2, Title = "Drop the old retry flag", Status = "skipped" },
                    new PlanLedgerTaskDto { TaskId = "t4", Ordinal = 3, Title = "Document the retry limits", Status = "pending" },
                ],
            },
        ]);

        var work = ContextMapping.Work(WorkContextCards.Apply(WorkContextCard.Empty(WorkContextPhase.Loading), read, null), "https://kcap.example.com/");
        var plan = ContextMapping.Plan(PlanBoard.From(plans)!);

        await Assert.That(JsonSerializer.SerializeToNode(work, IdeJsonContext.Default.WorkSection)!.ToJsonString()).IsEqualTo(fixture["work"]!.ToJsonString());
        await Assert.That(JsonSerializer.SerializeToNode(plan, IdeJsonContext.Default.PlanSection)!.ToJsonString()).IsEqualTo(fixture["plan"]!.ToJsonString());
    }

    static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
