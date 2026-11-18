using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Ide;
using Capacitor.Client;

namespace Capacitor.Cli.Tests.Unit.Ide;

public class SessionRowMappingTests {
    static AgentRow Row(AgentOrigin origin = AgentOrigin.Local, string status = "Running") => new(
        Key: "key-1", Origin: origin, Id: "id-1", SessionId: "session-1", Kind: "kind-1", Vendor: "vendor-1", Status: status,
        CreatedAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), RepoPath: "/repo-path", Title: "title-1", Model: "model-1",
        RequesterDisplay: "requester-1", WorktreePath: "/worktree-1", WorkLocation: "/work-location", BorrowedFrom: "/borrowed",
        MachineBadge: "badge-1", RepoGroupKey: "repo:group-key", RepoGroupLabel: "repo-label-1", CheckoutKey: "checkout-key",
        CheckoutLabel: "checkout-label-1",
        AwaitingInput: false, LiveSubagents: 3, OwnerName: "owner-1", OwnerIsViewer: true,
        LastActivityAt: new DateTime(2026, 1, 3, 4, 5, 6, DateTimeKind.Utc), EndedAt: new DateTime(2026, 1, 4, 5, 6, 7, DateTimeKind.Utc),
        IsStale: true, IsTerminalSession: false, Branch: "branch-1");

    static JsonNode Wire(AgentRow row) => JsonSerializer.SerializeToNode(SessionRowMapping.From(row), IdeJsonContext.Default.SessionRow)!;

    [Test]
    public async Task Every_row_field_maps() {
        var expected = JsonNode.Parse("""
            {
              "key": "key-1",
              "origin": "local",
              "id": "id-1",
              "session_id": "session-1",
              "kind": "kind-1",
              "vendor": "vendor-1",
              "status": "Running",
              "title": "title-1",
              "model": "model-1",
              "branch": "branch-1",
              "repo_label": "repo-label-1",
              "worktree_path": "/worktree-1",
              "created_at": "2026-01-02T03:04:05Z",
              "last_activity_at": "2026-01-03T04:05:06Z",
              "ended_at": "2026-01-04T05:06:07Z",
              "awaiting_input": false,
              "live_subagents": 3,
              "is_stale": true,
              "is_ended": false,
              "is_terminal_session": false,
              "owner_name": "owner-1",
              "owner_is_viewer": true,
              "machine_badge": "badge-1"
            }
            """);

        // The same row with every boolean flipped, so a mapping that swaps two of them fails one of the two.
        var flipped = Row(status: "Completed") with { AwaitingInput = true, IsStale = false, IsTerminalSession = true, OwnerIsViewer = false };
        var expectedFlipped = expected!.DeepClone();
        expectedFlipped["status"]              = "Completed";
        expectedFlipped["awaiting_input"]      = true;
        expectedFlipped["is_stale"]            = false;
        expectedFlipped["is_ended"]            = true;
        expectedFlipped["is_terminal_session"] = true;
        expectedFlipped["owner_is_viewer"]     = false;

        await Assert.That(Wire(Row()).ToJsonString()).IsEqualTo(expected.ToJsonString());
        await Assert.That(Wire(flipped).ToJsonString()).IsEqualTo(expectedFlipped.ToJsonString());
    }

    [Test]
    public async Task Times_are_sent_as_utc_whatever_their_kind() {
        var utc         = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var unspecified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);

        static string CreatedAt(DateTime value) => Wire(Row() with { CreatedAt = value })["created_at"]!.GetValue<string>();

        var mapped = SessionRowMapping.From(Row() with { CreatedAt = unspecified }).CreatedAt;
        await Assert.That(mapped.DateTime).IsEqualTo(unspecified);
        await Assert.That(mapped.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(CreatedAt(unspecified)).IsEqualTo("2026-01-02T03:04:05Z");
        await Assert.That(CreatedAt(utc)).IsEqualTo("2026-01-02T03:04:05Z");
        // Tells a conversion from a reinterpretation only where the local zone is not UTC.
        await Assert.That(CreatedAt(utc.ToLocalTime())).IsEqualTo("2026-01-02T03:04:05Z");
    }

    [Test]
    public async Task Origins_are_lowercase() {
        foreach (var origin in Enum.GetValues<AgentOrigin>())
            await Assert.That(SessionRowMapping.From(Row(origin)).Origin).IsEqualTo(origin.ToString().ToLowerInvariant());
    }

    [Test]
    public async Task Only_completed_rows_are_marked_ended() {
        await Assert.That(SessionRowMapping.From(Row(AgentOrigin.Server, "Completed")).IsEnded).IsTrue();
        await Assert.That(SessionRowMapping.From(Row(AgentOrigin.Local, "Completed")).IsEnded).IsTrue();
        await Assert.That(SessionRowMapping.From(Row(AgentOrigin.Local, "Failed")).IsEnded).IsFalse();
        await Assert.That(SessionRowMapping.From(Row(AgentOrigin.Local, "Running")).IsEnded).IsFalse();
    }
}
