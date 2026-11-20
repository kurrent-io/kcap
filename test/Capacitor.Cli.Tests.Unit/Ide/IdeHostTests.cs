using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Capacitor.Cli.Ide;
using Capacitor.Client;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Ide;

/// <summary>The host over a live loop: requests go in one line at a time and every message it writes is read back in order.</summary>
public class IdeHostTests {
    const string CliVersion = "9.8.7";
    const string MachineHere = "mach-here";
    // The host refuses a root that is not fully qualified, and on Windows that needs a drive.
    static readonly string App = OperatingSystem.IsWindows() ? @"C:\src\app" : "/src/app";
    static readonly string Lib = OperatingSystem.IsWindows() ? @"C:\src\lib" : "/src/lib";

    sealed class Requests : TextReader {
        readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public void Send(string line) => _lines.Writer.TryWrite(line);

        public void End() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _lines.Reader.WaitToReadAsync(cancellationToken) && _lines.Reader.TryRead(out var line) ? line : null;
    }

    sealed class Messages : TextWriter {
        readonly Channel<JsonObject> _messages = Channel.CreateUnbounded<JsonObject>();

        public override Encoding Encoding => Encoding.UTF8;

        /// <summary>Runs inside the loop's write, before the message can be read back.</summary>
        public Action<JsonObject>? OnWrite { get; set; }

        public override Task WriteAsync(string? value) {
            foreach (var line in value!.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                var message = JsonNode.Parse(line)!.AsObject();
                OnWrite?.Invoke(message);
                _messages.Writer.TryWrite(message);
            }

            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JsonObject> NextAsync(string step) => _messages.Reader.ReadAsync().AsTask().WithinDeadlineAsync(step);

        public bool AnyUnread => _messages.Reader.TryPeek(out _);
    }

    sealed class Host : IDisposable {
        readonly IdeRpcLoop _loop;
        readonly Task _run;
        int _lastId;

        public Host() {
            Ide = new IdeHost(
                Daemon, Lane, Sessions, Directory, new RepoIdentityResolver(_ => null),
                root => { OnResolveRoot?.Invoke(); return root; }, MachineHere, () => Reconnect(), Time, CliVersion);
            _loop = new IdeRpcLoop(Input, Output);
            Ide.Attach(_loop);
            _run = _loop.RunAsync(CancellationToken.None);
        }

        public FakeDaemonClientService Daemon { get; } = new();
        public FakeServerLane Lane { get; } = new();
        public FakeServerSessionsService Sessions { get; } = new();
        public FakeAgentDirectory Directory { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public Requests Input { get; } = new();
        public Messages Output { get; } = new();
        public IdeHost Ide { get; }
        public Action? OnResolveRoot { get; set; }
        public Func<Task> Reconnect { get; set; } = () => Task.CompletedTask;

        /// <summary>Notifications read while waiting for something else, in the order they arrived.</summary>
        public List<JsonObject> Seen { get; } = [];

        public int LastId => _lastId;

        public async Task<JsonObject> CallAsync(string method, JsonNode? parameters) {
            var id      = ++_lastId;
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null) request["params"] = parameters.DeepClone();
            Input.Send(request.ToJsonString());

            return await UntilAsync(m => m["id"]?.GetValue<int>() == id, $"the reply to {method}");
        }

        public async Task<JsonObject> UntilAsync(Func<JsonObject, bool> wanted, string step) {
            while (true) {
                var message = await Output.NextAsync(step);
                if (wanted(message)) return message;

                Seen.Add(message);
            }
        }

        /// <summary>The <c>connection/changed</c> that follows the reply.</summary>
        public async Task<JsonObject> InitializeAsync() {
            await CallAsync("initialize", Fixture("initialize.request.json")["params"]);

            return await Output.NextAsync("connection/changed after initialize");
        }

        /// <summary>The subscription's snapshot.</summary>
        public async Task<JsonObject> SubscribeAsync(string subscriptionId, params string[] roots) {
            var reply = await CallAsync("sessions/subscribe", new JsonObject {
                ["subscription_id"] = subscriptionId,
                ["git_roots"]       = new JsonArray([.. roots.Select(r => (JsonNode?)JsonValue.Create(r))]),
            });
            await Assert.That(reply["result"]!.ToJsonString()).IsEqualTo("{}");

            return await UntilAsync(m => Method(m) == "sessions/snapshot" && Subscription(m) == subscriptionId, $"the snapshot of {subscriptionId}");
        }

        /// <summary>Everything the host queued before this call, written: a scope announcement goes through the same
        /// queue, after it.</summary>
        public async Task<List<JsonObject>> SettleAsync() {
            await CallAsync("sessions/set_scope", new JsonObject { ["scope"] = Sessions.Scope == RailScope.Team ? "team" : "mine" });
            await UntilAsync(m => Method(m) == "sessions/scope_changed", "the scope announcement");
            List<JsonObject> seen = [.. Seen];
            Seen.Clear();

            return seen;
        }

        public Task EndAsync() {
            Input.End();

            return _run.WithinDeadlineAsync("the loop to end");
        }

        public void Dispose() {
            Input.End();
            Ide.Dispose();
            _loop.Dispose();
            Directory.Dispose();
            Sessions.Dispose();
        }
    }

    static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-fixtures", "ide-protocol", name)))!.AsObject();

    static string? Method(JsonObject message) => message["method"]?.GetValue<string>();

    static string? Subscription(JsonObject message) => message["params"]?["subscription_id"]?.GetValue<string>();

    static IEnumerable<JsonObject> Changes(IEnumerable<JsonObject> messages, string subscriptionId) =>
        messages.Where(m => Method(m) == "sessions/changed" && Subscription(m) == subscriptionId);

    static string[] Keys(JsonNode? rows) => [.. rows!.AsArray().Select(r => r!["key"]!.GetValue<string>()).Order()];

    static string[] Strings(JsonNode? values) => [.. values!.AsArray().Select(v => v!.GetValue<string>()).Order()];

    static AgentRow Row(string id, string repoPath, string? title = null) => new(
        Key: $"local:{id}", Origin: AgentOrigin.Local, Id: id, SessionId: id, Kind: "agent", Vendor: "claude", Status: "Running",
        CreatedAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), RepoPath: repoPath, Title: title, Model: null,
        RequesterDisplay: null, WorktreePath: null, WorkLocation: null, BorrowedFrom: null, MachineBadge: null,
        RepoGroupKey: $"path:{repoPath}", RepoGroupLabel: Path.GetFileName(repoPath), CheckoutKey: repoPath, CheckoutLabel: "");

    static AgentRow ServerRow(string id, string repoGroupKey, string repoPath, string recordedOn) => Row(id, repoPath) with {
        Key = $"server:{id}", Origin = AgentOrigin.Server, Kind = "session", RepoGroupKey = repoGroupKey,
        Recording = new RepoSessionRecordingDto { MachineId = recordedOn },
    };

    /// <summary>The same envelope and the same <c>params</c> members as the fixture.</summary>
    static async Task ShapedLikeAsync(JsonObject message, string fixture) {
        var expected = Fixture(fixture);
        await Assert.That(message.Select(p => p.Key)).IsEquivalentTo(expected.Select(p => p.Key));
        await Assert.That(message["params"]!.AsObject().Select(p => p.Key)).IsEquivalentTo(expected["params"]!.AsObject().Select(p => p.Key));
    }

    [Test]
    public async Task Initialize_answers_version_and_capabilities() {
        using var host = new Host();

        var reply = await host.CallAsync("initialize", Fixture("initialize.request.json")["params"]);

        var expected = Fixture("initialize.result.json");
        expected["result"]!["cli_version"] = CliVersion;
        await Assert.That(JsonNode.DeepEquals(reply, expected)).IsTrue();
    }

    [Test]
    public async Task Initialize_reports_the_current_scope() {
        using var host = new Host();
        host.Sessions.Scope = RailScope.Team;

        var reply = await host.CallAsync("initialize", Fixture("initialize.request.json")["params"]);

        await Assert.That(reply["result"]!["scope"]!.GetValue<string>()).IsEqualTo("team");
    }

    [Test]
    public async Task Initialize_with_another_protocol_version_is_refused() {
        using var host = new Host();
        var parameters = Fixture("initialize.request.json")["params"]!;
        parameters["protocol_version"] = 2;

        var reply = await host.CallAsync("initialize", parameters);

        var expected = Fixture("initialize.error.unsupported.json");
        expected["error"]!["data"]!["cli_version"] = CliVersion;
        await Assert.That(JsonNode.DeepEquals(reply, expected)).IsTrue();
    }

    [Test]
    public async Task A_method_before_initialize_is_refused_with_32002() {
        using var host = new Host();

        host.Input.Send(Fixture("sessions.subscribe.request.json").ToJsonString());
        var reply = await host.UntilAsync(m => m.ContainsKey("id"), "the reply to the subscribe");

        await Assert.That(JsonNode.DeepEquals(reply, Fixture("error.not_initialized.json"))).IsTrue();
        await Assert.That(host.Seen).IsEmpty();
    }

    [Test]
    public async Task Connection_state_follows_initialize() {
        using var host = new Host();
        host.Daemon.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));

        var reply = await host.CallAsync("initialize", Fixture("initialize.request.json")["params"]);
        var next  = await host.Output.NextAsync("the message after the reply");

        await Assert.That(reply["result"]).IsNotNull();
        await Assert.That(host.Seen).IsEmpty();
        await Assert.That(Method(next)).IsEqualTo("connection/changed");
        await Assert.That(next["params"]!.ToJsonString()).IsEqualTo(
            """{"daemon":{"state":"unreachable","name":"daemon-a","detail":"daemon_unreachable"},"server":{"state":"dormant"}}""");
    }

    [Test]
    public async Task Status_changes_after_initialize_are_announced() {
        using var host = new Host();
        host.Daemon.DaemonName = "dev-laptop";
        await host.InitializeAsync();

        host.Daemon.StatusSubject.OnNext(new(AttachState.Connected, null, null));
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Retrying, "connection refused"));
        var seen = await host.SettleAsync();

        var announced = seen.Where(m => Method(m) == "connection/changed").ToList();
        await Assert.That(announced.Count).IsEqualTo(2);
        await Assert.That(JsonNode.DeepEquals(announced[^1], Fixture("connection.changed.json"))).IsTrue();
    }

    [Test]
    public async Task Subscribe_sends_a_snapshot_of_the_projects_rows_only() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate([Row("a1", App), Row("a2", Path.Combine(App, "src")), Row("l1", Lib)]);
        await host.InitializeAsync();

        var snapshot = await host.SubscribeAsync("sub-1", App);

        await ShapedLikeAsync(snapshot, "sessions.snapshot.json");
        await Assert.That(Keys(snapshot["params"]!["rows"])).IsEquivalentTo(["local:a1", "local:a2"]);
    }

    [Test]
    public async Task A_row_of_another_repository_is_never_sent() {
        using var host = new Host();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", App);

        host.Directory.Rows.AddOrUpdate(Row("l1", Lib));
        host.Time.Advance(TimeSpan.FromMilliseconds(200));
        var seen = await host.SettleAsync();

        await Assert.That(seen.Where(m => Method(m) == "sessions/changed")).IsEmpty();
    }

    /// <summary>The session recorded here under another repository name is the control: it shares the path and the key.</summary>
    [Test]
    public async Task A_foreign_session_at_the_same_path_on_another_machine_is_not_sent() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate([
            ServerRow("there", "repo:acme/other", App, recordedOn: "mach-there"),
            ServerRow("here", "repo:acme/other", App, recordedOn: MachineHere),
        ]);
        await host.InitializeAsync();

        var snapshot = await host.SubscribeAsync("sub-1", App);

        await Assert.That(Keys(snapshot["params"]!["rows"])).IsEquivalentTo(["server:here"]);
    }

    [Test]
    public async Task An_updated_row_arrives_as_an_upsert_and_a_removed_one_as_a_remove() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate([Row("a1", App), Row("a2", App)]);
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", App);

        host.Directory.Rows.AddOrUpdate(Row("a1", App, title: "Renamed"));
        host.Directory.Rows.RemoveKey("local:a2");
        host.Time.Advance(TimeSpan.FromMilliseconds(100));
        var changed = Changes(await host.SettleAsync(), "sub-1").Single();

        await ShapedLikeAsync(changed, "sessions.changed.json");
        await Assert.That(Keys(changed["params"]!["upserts"])).IsEquivalentTo(["local:a1"]);
        await Assert.That(changed["params"]!["upserts"]![0]!["title"]!.GetValue<string>()).IsEqualTo("Renamed");
        await Assert.That(Strings(changed["params"]!["removes"])).IsEquivalentTo(["local:a2"]);
    }

    [Test]
    public async Task A_row_that_leaves_the_project_arrives_as_a_remove() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate(Row("a1", App));
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", App);

        host.Directory.Rows.AddOrUpdate(Row("a1", Lib));
        host.Time.Advance(TimeSpan.FromMilliseconds(100));
        var changed = Changes(await host.SettleAsync(), "sub-1").Single();

        await Assert.That(changed["params"]!["upserts"]!.AsArray()).IsEmpty();
        await Assert.That(Strings(changed["params"]!["removes"])).IsEquivalentTo(["local:a1"]);
    }

    [Test]
    public async Task An_update_that_changes_nothing_on_the_wire_is_not_sent() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate(Row("a1", App));
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", App);

        host.Directory.Rows.AddOrUpdate(Row("a1", App) with { CheckoutKey = "elsewhere" });
        host.Time.Advance(TimeSpan.FromMilliseconds(100));

        await Assert.That(Changes(await host.SettleAsync(), "sub-1")).IsEmpty();
    }

    [Test]
    public async Task Two_subscriptions_each_get_their_own_rows() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate([Row("a1", App), Row("l1", Lib)]);
        await host.InitializeAsync();

        var app = await host.SubscribeAsync("sub-app", App);
        var lib = await host.SubscribeAsync("sub-lib", Lib);
        host.Directory.Rows.AddOrUpdate([Row("a2", App), Row("l2", Lib)]);
        host.Time.Advance(TimeSpan.FromMilliseconds(100));
        var seen = await host.SettleAsync();

        await Assert.That(Keys(app["params"]!["rows"])).IsEquivalentTo(["local:a1"]);
        await Assert.That(Keys(lib["params"]!["rows"])).IsEquivalentTo(["local:l1"]);
        await Assert.That(Keys(Changes(seen, "sub-app").Single()["params"]!["upserts"])).IsEquivalentTo(["local:a2"]);
        await Assert.That(Keys(Changes(seen, "sub-lib").Single()["params"]!["upserts"])).IsEquivalentTo(["local:l2"]);
    }

    [Test]
    public async Task Unsubscribe_stops_changes() {
        using var host = new Host();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", App);

        var reply = await host.CallAsync("sessions/unsubscribe", Fixture("sessions.unsubscribe.request.json")["params"]);
        host.Directory.Rows.AddOrUpdate(Row("a1", App));
        host.Time.Advance(TimeSpan.FromMilliseconds(200));
        var seen = await host.SettleAsync();

        await Assert.That(reply["result"]!.ToJsonString()).IsEqualTo("{}");
        await Assert.That(seen.Where(m => Method(m) == "sessions/changed")).IsEmpty();
    }

    [Test]
    public async Task Unsubscribe_drops_a_batch_already_pending() {
        using var host = new Host();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", App);

        host.Directory.Rows.AddOrUpdate(Row("a1", App));
        host.Time.Advance(TimeSpan.FromMilliseconds(50));
        await host.CallAsync("sessions/unsubscribe", new JsonObject { ["subscription_id"] = "sub-1" });
        host.Time.Advance(TimeSpan.FromMilliseconds(100));
        var seen = await host.SettleAsync();

        await Assert.That(Changes(seen, "sub-1")).IsEmpty();
    }

    [Test]
    public async Task A_relative_git_root_is_refused() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate(Row("a1", App));
        await host.InitializeAsync();

        var reply = await host.CallAsync("sessions/subscribe", new JsonObject {
            ["subscription_id"] = "sub-1",
            ["git_roots"]       = new JsonArray(App, Path.Combine("src", "app")),
        });

        await Assert.That(reply["error"]!["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.InvalidParams);
        await Assert.That(host.Seen.Where(m => Method(m) == "sessions/snapshot")).IsEmpty();
    }

    [Test]
    public async Task A_blank_git_root_is_ignored() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate(Row("a1", App));
        await host.InitializeAsync();

        var snapshot = await host.SubscribeAsync("sub-1", "", "  ", App);

        await Assert.That(Keys(snapshot["params"]!["rows"])).IsEquivalentTo(["local:a1"]);
    }

    [Test, RunOn(OS.Windows)]
    public async Task A_forward_slash_root_is_accepted_on_windows() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate(Row("a1", App));
        await host.InitializeAsync();

        var snapshot = await host.SubscribeAsync("sub-1", App.Replace('\\', '/'));

        await Assert.That(Keys(snapshot["params"]!["rows"])).IsEquivalentTo(["local:a1"]);
    }

    [Test]
    public async Task Unsubscribe_of_an_unknown_id_answers_an_empty_result() {
        using var host = new Host();
        await host.InitializeAsync();

        var reply = await host.CallAsync("sessions/unsubscribe", new JsonObject { ["subscription_id"] = "never" });

        await Assert.That(reply["result"]!.ToJsonString()).IsEqualTo("{}");
    }

    [Test]
    public async Task Changes_within_100_ms_arrive_as_one_message() {
        using var host = new Host();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", App);

        host.Directory.Rows.AddOrUpdate(Row("a1", App));
        host.Time.Advance(TimeSpan.FromMilliseconds(50));
        var early = await host.SettleAsync();
        host.Directory.Rows.AddOrUpdate(Row("a2", App));
        host.Time.Advance(TimeSpan.FromMilliseconds(50));
        var changed = Changes(await host.SettleAsync(), "sub-1").Single();

        await Assert.That(Changes(early, "sub-1")).IsEmpty();
        await Assert.That(Keys(changed["params"]!["upserts"])).IsEquivalentTo(["local:a1", "local:a2"]);
    }

    [Test]
    public async Task Local_rows_are_listed_while_the_server_is_signed_out() {
        using var host = new Host();
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.SignedOut, "signed out"));
        host.Directory.Rows.AddOrUpdate(Row("a1", App));

        var connection = await host.InitializeAsync();
        var snapshot   = await host.SubscribeAsync("sub-1", App);

        await Assert.That(connection["params"]!["server"]!["state"]!.GetValue<string>()).IsEqualTo("signed_out");
        await Assert.That(Keys(snapshot["params"]!["rows"])).IsEquivalentTo(["local:a1"]);
        await Assert.That(snapshot["params"]!["rows"]![0]!["origin"]!.GetValue<string>()).IsEqualTo("local");
    }

    [Test]
    public async Task Subscribe_with_no_git_roots_sends_an_empty_snapshot() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate(Row("a1", App));
        await host.InitializeAsync();

        var snapshot = await host.SubscribeAsync("sub-1");

        await Assert.That(snapshot["params"]!["rows"]!.AsArray()).IsEmpty();
    }

    [Test]
    public async Task Subscribing_again_under_the_same_id_replaces_the_subscription() {
        using var host = new Host();
        host.Directory.Rows.AddOrUpdate([Row("a1", App), Row("l1", Lib)]);
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", App);

        var again = await host.SubscribeAsync("sub-1", Lib);
        host.Directory.Rows.AddOrUpdate([Row("a2", App), Row("l2", Lib)]);
        host.Time.Advance(TimeSpan.FromMilliseconds(100));
        var changed = Changes(await host.SettleAsync(), "sub-1").Single();

        await Assert.That(Keys(again["params"]!["rows"])).IsEquivalentTo(["local:l1"]);
        await Assert.That(Keys(changed["params"]!["upserts"])).IsEquivalentTo(["local:l2"]);
    }

    [Test]
    [Arguments(null)]
    [Arguments("""{"git_roots":["/src/app"]}""")]
    [Arguments("""{"subscription_id":"sub-1"}""")]
    [Arguments("""{"subscription_id":1,"git_roots":[]}""")]
    [Arguments("""{"subscription_id":"sub-1","git_roots":"/src/app"}""")]
    [Arguments("""{"subscription_id":"sub-1","git_roots":[null]}""")]
    [Arguments("""{"subscription_id":"sub-1","git_roots":[1]}""")]
    public async Task Subscribe_with_missing_or_wrong_params_answers_32602(string? parameters) {
        using var host = new Host();
        await host.InitializeAsync();

        var reply = await host.CallAsync("sessions/subscribe", parameters is null ? null : JsonNode.Parse(parameters));

        await Assert.That(reply["error"]!["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.InvalidParams);
    }

    [Test]
    public async Task Set_scope_sets_the_service_scope_and_announces_it() {
        using var host = new Host();
        await host.InitializeAsync();

        var reply = await host.CallAsync("sessions/set_scope", Fixture("sessions.set_scope.request.json")["params"]);
        var next  = await host.Output.NextAsync("the scope announcement");

        await Assert.That(reply["result"]!.ToJsonString()).IsEqualTo("{}");
        await Assert.That(host.Sessions.Scope).IsEqualTo(RailScope.Team);
        await Assert.That(JsonNode.DeepEquals(next, Fixture("sessions.scope_changed.json"))).IsTrue();
    }

    [Test]
    public async Task Set_scope_with_an_unknown_scope_answers_32602() {
        using var host = new Host();
        await host.InitializeAsync();

        var reply = await host.CallAsync("sessions/set_scope", new JsonObject { ["scope"] = "everyone" });

        await Assert.That(reply["error"]!["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.InvalidParams);
        await Assert.That(host.Sessions.Scope).IsEqualTo(RailScope.Mine);
    }

    /// <summary>One row lands while the subscription's roots are resolved, another while its reply is being written:
    /// each must reach the client in the snapshot or in a later change.</summary>
    [Test]
    public async Task A_row_added_while_subscribing_is_not_lost() {
        using var host = new Host();
        await host.InitializeAsync();
        host.OnResolveRoot = () => host.Directory.Rows.AddOrUpdate(Row("during-resolve", App));
        var subscribeId = host.LastId + 1;
        host.Output.OnWrite = m => {
            if (m["id"]?.GetValue<int>() == subscribeId) host.Directory.Rows.AddOrUpdate(Row("during-reply", App));
        };

        var snapshot = await host.SubscribeAsync("sub-1", App);
        host.Time.Advance(TimeSpan.FromMilliseconds(100));
        var seen = await host.SettleAsync();

        string[] sent = [
            .. Keys(snapshot["params"]!["rows"]),
            .. Changes(seen, "sub-1").SelectMany(m => Keys(m["params"]!["upserts"])),
        ];
        await Assert.That(sent).Contains("local:during-resolve");
        await Assert.That(sent).Contains("local:during-reply");
    }

    [Test]
    [Arguments(null)]
    [Arguments("{}")]
    public async Task Retry_answers_then_asks_the_host_to_reconnect(string? parameters) {
        using var host = new Host();
        await host.InitializeAsync();
        var order = new ConcurrentQueue<string>();
        var retryId = host.LastId + 1;
        host.Output.OnWrite = m => {
            if (m["id"]?.GetValue<int>() == retryId) order.Enqueue("reply");
        };
        host.Reconnect = () => {
            order.Enqueue("reconnect");
            return Task.CompletedTask;
        };

        var reply = await host.CallAsync("connection/retry", parameters is null ? null : JsonNode.Parse(parameters));
        // The follow-up runs before the next request is read, so this reply means it has run.
        await host.CallAsync("sessions/unsubscribe", new JsonObject { ["subscription_id"] = "never" });

        await Assert.That(reply["result"]!.ToJsonString()).IsEqualTo("{}");
        await Assert.That(order).IsEquivalentTo(["reply", "reconnect"], CollectionOrdering.Matching);
        await Assert.That(host.Seen.Where(m => Method(m) == "connection/changed")).IsEmpty();
    }

    [Test]
    public async Task Retry_before_initialize_is_refused_with_32002() {
        using var host = new Host();
        var calls = 0;
        host.Reconnect = () => {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        };

        var reply = await host.CallAsync("connection/retry", null);
        await host.CallAsync("sessions/unsubscribe", new JsonObject { ["subscription_id"] = "never" });

        await Assert.That(reply["error"]!["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.NotInitialized);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task A_failing_reconnect_does_not_end_the_loop() {
        using var host = new Host();
        await host.InitializeAsync();
        var calls = 0;
        host.Reconnect = () => {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("reconnect failed");
        };

        var retry = await host.CallAsync("connection/retry", null);
        var next  = await host.CallAsync("sessions/unsubscribe", new JsonObject { ["subscription_id"] = "never" });

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(retry["result"]!.ToJsonString()).IsEqualTo("{}");
        await Assert.That(next["result"]!.ToJsonString()).IsEqualTo("{}");
    }

    [Test]
    public async Task Dispose_stops_all_notifications() {
        using var host = new Host();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", App);
        host.Directory.Rows.AddOrUpdate(Row("a1", App));

        host.Ide.Dispose();
        host.Directory.Rows.AddOrUpdate(Row("a2", App));
        host.Daemon.StatusSubject.OnNext(new(AttachState.Connected, null, null));
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        host.Time.Advance(TimeSpan.FromMilliseconds(200));
        await host.CallAsync("sessions/unsubscribe", new JsonObject { ["subscription_id"] = "never" });
        await host.EndAsync();

        await Assert.That(host.Seen).IsEmpty();
        await Assert.That(host.Output.AnyUnread).IsFalse();
        await Assert.That(host.Daemon.StatusSubject.HasObservers).IsFalse();
        await Assert.That(host.Lane.StatusSubject.HasObservers).IsFalse();
    }
}
