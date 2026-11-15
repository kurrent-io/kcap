using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Cli.Ide;
using Capacitor.Client;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Ide;

/// <summary>The work-context host beside the session host, over one live loop and one outbox: requests go in one line
/// at a time and every message written is read back in order.</summary>
public class ContextHostTests {
    const string Session = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    const string OtherSession = "99998888777766665555444433332222";
    const string LoadingWork =
        """{"phase":"loading","stale":false,"title":"","blocked_by":[],"cycle":"none","parts":[],"issues":[],"people":[],"session_count":0}""";
    const string LoadingPlan = """{"state":"loading","tasks":[],"done":0,"total":0}""";

    /// <summary>Answers reads in the order they arrive, from what was queued, then with <see cref="Default"/>. A gated read
    /// ignores cancellation, as a request already on the wire does. Like the server sources, it keeps the last ready read
    /// per session and forgets every one on a signed-out read.</summary>
    class Script<T>(T initial, Func<T, bool?> remember, Func<T, bool> signsOut) where T : class {
        readonly Lock _lock = new();
        readonly Queue<Func<Task<T>>> _answers = new();
        readonly List<string> _requested = [];
        readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];
        readonly LastReads<T> _last = new();
        T _default = initial;

        public T Default {
            get { lock (_lock) return _default; }
            set { lock (_lock) _default = value; }
        }

        public string[] Requested {
            get { lock (_lock) return [.. _requested]; }
        }

        public void Answer(T read) {
            lock (_lock) _answers.Enqueue(() => Task.FromResult(read));
        }

        public void Throw(Exception error) {
            lock (_lock) _answers.Enqueue(() => Task.FromException<T>(error));
        }

        public TaskCompletionSource<T> Gate() {
            var gate = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _answers.Enqueue(() => gate.Task);
            return gate;
        }

        /// <summary>Completes once this many reads have been asked for: reads run on the thread pool, so a gate queued
        /// before an earlier read has arrived can be taken by a later one.</summary>
        public Task RequestedAsync(int count) {
            lock (_lock) {
                if (_requested.Count >= count) return Task.CompletedTask;

                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, reached));

                return reached.Task;
            }
        }

        public T? Cached(string sessionId) => _last.Get(sessionId);

        public void InvalidateAuthentication() => _last.Clear();

        public async Task<T> ReadAsync(string sessionId, CancellationToken ct) {
            Func<Task<T>>? answer;
            T fallback;
            lock (_lock) {
                _requested.Add(sessionId);
                _answers.TryDequeue(out answer);
                fallback = _default;
                foreach (var waiter in _waiters.Where(w => w.Count <= _requested.Count)) waiter.Reached.TrySetResult();
                _waiters.RemoveAll(w => w.Reached.Task.IsCompleted);
            }

            var read = await _last.RecordAsync(sessionId, () => answer?.Invoke() ?? Task.FromResult(fallback), remember);
            if (signsOut(read)) _last.Clear();

            return read;
        }
    }

    sealed class FakeWork() : Script<WorkContextRead>(
        Ready("Alpha"),
        read => read.Kind switch { WorkContextReadKind.Ready => true, WorkContextReadKind.Unreachable => null, _ => false },
        read => read.Kind == WorkContextReadKind.SignedOut), IWorkContextSource;

    sealed class FakePlans() : Script<SessionPlansRead>(
        Plan(("Write the test", "completed"), ("Make it pass", "in_progress")),
        read => read.Kind switch { SessionPlansReadKind.Ready => true, SessionPlansReadKind.Unreachable => null, _ => false },
        read => read.Kind == SessionPlansReadKind.SignedOut), IPlanSource;

    sealed class Host : IDisposable {
        readonly IdeRpcLoop _loop;
        readonly Task _run;
        int _lastId;

        public Host() {
            Ide = new IdeHost(
                Daemon, Lane, Sessions, Directory, new RepoIdentityResolver(_ => null), root => root, "mach-here",
                () => Task.CompletedTask, Time, "9.8.7");
            Context = new ContextHost(Work, Plans, Lane.Status, "https://kcap.example.test/", Time, Ide.Outbox, () => Ide.IsInitialized);
            _loop = new IdeRpcLoop(Input, Output);
            Ide.Attach(_loop);
            Context.Attach(_loop);
            _run = _loop.RunAsync(CancellationToken.None);
        }

        public FakeDaemonClientService Daemon { get; } = new();
        public FakeServerLane Lane { get; } = new();
        public FakeServerSessionsService Sessions { get; } = new();
        public FakeAgentDirectory Directory { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public FakeWork Work { get; } = new();
        public FakePlans Plans { get; } = new();
        public IdeHostTests.Requests Input { get; } = new();
        public IdeHostTests.Messages Output { get; } = new();
        public IdeHost Ide { get; }
        public ContextHost Context { get; }

        /// <summary>Notifications read while waiting for something else, in the order they arrived.</summary>
        public List<JsonObject> Seen { get; } = [];

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

        public async Task InitializeAsync() {
            await CallAsync("initialize", Fixture("initialize.request.json")["params"]);
            await Output.NextAsync("connection/changed after initialize");
        }

        /// <summary>The subscription's snapshot.</summary>
        public async Task<JsonObject> SubscribeAsync(string subscriptionId, string sessionId) {
            var reply = await CallAsync("context/subscribe", new JsonObject { ["subscription_id"] = subscriptionId, ["session_id"] = sessionId });
            await Assert.That(reply["result"]!.ToJsonString()).IsEqualTo("{}");

            return await UntilAsync(m => Method(m) == "context/snapshot" && Subscription(m) == subscriptionId, $"the snapshot of {subscriptionId}");
        }

        public Task<JsonObject> RefreshAsync(string subscriptionId) =>
            CallAsync("context/refresh", new JsonObject { ["subscription_id"] = subscriptionId });

        /// <summary>Every read the subscription has started, settled, including a follow-up a settling read starts.</summary>
        public async Task IdleAsync(string subscriptionId) {
            while (Context.PendingReadsForTesting(subscriptionId) is { IsCompleted: false } pending)
                await pending.WithinDeadlineAsync($"the reads of {subscriptionId}");
        }

        /// <summary>Both sources have been asked for this many reads, so a gate queued from here on goes to a later one.</summary>
        public async Task ReadsRequestedAsync(int count) {
            await Work.RequestedAsync(count).WithinDeadlineAsync($"work read {count}");
            await Plans.RequestedAsync(count).WithinDeadlineAsync($"plan read {count}");
        }

        /// <summary>Everything queued before this call, written: an unsubscribe answers once the queue ahead of it drains.</summary>
        public async Task<List<JsonObject>> SettleAsync() {
            await CallAsync("context/unsubscribe", new JsonObject { ["subscription_id"] = "settle" });
            List<JsonObject> seen = [.. Seen];
            Seen.Clear();

            return seen;
        }

        public void Dispose() {
            Input.End();
            Context.Dispose();
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

    static List<JsonObject> Changes(IEnumerable<JsonObject> messages, string subscriptionId) =>
        [.. messages.Where(m => Method(m) == "context/changed" && Subscription(m) == subscriptionId)];

    /// <summary>The section as the client holds it after the snapshot and these changes.</summary>
    static JsonNode Latest(JsonObject snapshot, IEnumerable<JsonObject> changes, string section) =>
        changes.Select(m => m["params"]![section]).LastOrDefault(s => s is not null) ?? snapshot["params"]![section]!;

    static string Text(JsonNode? node, string member) => node![member]!.GetValue<string>();

    static SessionWorkItemAssignmentDto Assignment(string id) => new() { WorkItemId = id, Label = id, IsPrimary = true };

    static WorkContextRead Ready(string title) => new(
        WorkContextReadKind.Ready, [Assignment("wi-1")], Assignment("wi-1"), new WorkItemDto { WorkItemId = "wi-1", Title = title },
        new WorkItemTopologyDto(), null, false, false, false, null);

    static readonly WorkContextRead NoWorkItem = new(WorkContextReadKind.Ready, [], null, null, null, null, false, false, false, null);

    static SessionPlansRead Plan(params (string Title, string Status)[] tasks) => new(SessionPlansReadKind.Ready, [
        new SessionPlanDto {
            PlanId    = "plan-1",
            IsCurrent = true,
            Tasks     = [.. tasks.Select((task, i) => new PlanLedgerTaskDto { TaskId = $"t{i + 1}", Ordinal = i, Title = task.Title, Status = task.Status })],
        },
    ]);

    [Test]
    public async Task Subscribe_answers_then_sends_a_snapshot_with_both_sections() {
        using var host = new Host();
        await host.InitializeAsync();

        var reply    = await host.CallAsync("context/subscribe", Fixture("context.subscribe.request.json")["params"]);
        var snapshot = await host.Output.NextAsync("the message after the reply");

        var expected = Fixture("context.snapshot.json");
        await Assert.That(reply["result"]!.ToJsonString()).IsEqualTo("{}");
        await Assert.That(host.Seen).IsEmpty();
        await Assert.That(snapshot.Select(p => p.Key)).IsEquivalentTo(expected.Select(p => p.Key));
        await Assert.That(Method(snapshot)).IsEqualTo("context/snapshot");
        await Assert.That(snapshot["params"]!.AsObject().Select(p => p.Key)).IsEquivalentTo(expected["params"]!.AsObject().Select(p => p.Key));
        await Assert.That(Subscription(snapshot)).IsEqualTo("ctx-1");
    }

    [Test]
    public async Task A_snapshot_before_any_read_is_loading_in_both_sections() {
        using var host = new Host();
        host.Work.Gate();
        host.Plans.Gate();
        await host.InitializeAsync();

        var snapshot = await host.SubscribeAsync("sub-1", Session);

        await Assert.That(snapshot["params"]!["work"]!.ToJsonString()).IsEqualTo(LoadingWork);
        await Assert.That(snapshot["params"]!["plan"]!.ToJsonString()).IsEqualTo(LoadingPlan);
    }

    [Test]
    public async Task An_unchanged_read_sends_nothing() {
        using var host = new Host();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        var first = Changes(await host.SettleAsync(), "sub-1");

        await host.RefreshAsync("sub-1");
        await host.IdleAsync("sub-1");
        var again = Changes(await host.SettleAsync(), "sub-1");

        await Assert.That(first).IsNotEmpty();
        await Assert.That(host.Work.Requested.Length).IsEqualTo(2);
        await Assert.That(host.Plans.Requested.Length).IsEqualTo(2);
        await Assert.That(again).IsEmpty();
    }

    [Test]
    public async Task A_changed_plan_sends_only_the_plan_section() {
        using var host = new Host();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        await host.SettleAsync();

        host.Plans.Default = Plan(("Write the test", "completed"), ("Make it pass", "completed"));
        await host.RefreshAsync("sub-1");
        await host.IdleAsync("sub-1");
        var changed = Changes(await host.SettleAsync(), "sub-1").Single();

        await Assert.That(changed["params"]!.AsObject().Select(p => p.Key)).IsEquivalentTo(["subscription_id", "plan"]);
        await Assert.That(changed["params"]!["plan"]!["done"]!.GetValue<int>()).IsEqualTo(2);
    }

    [Test]
    public async Task A_poll_tick_reads_both_sections_again() {
        using var host = new Host();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");

        host.Time.Advance(WorkContextTiming.PollInterval - TimeSpan.FromMilliseconds(1));
        await host.IdleAsync("sub-1");
        var beforeTick = (host.Work.Requested.Length, host.Plans.Requested.Length);
        host.Time.Advance(TimeSpan.FromMilliseconds(1));
        await host.IdleAsync("sub-1");

        await Assert.That(beforeTick).IsEqualTo((1, 1));
        await Assert.That(host.Work.Requested).IsEquivalentTo([Session, Session]);
        await Assert.That(host.Plans.Requested).IsEquivalentTo([Session, Session]);
    }

    [Test]
    public async Task Refresh_during_a_read_runs_one_follow_up() {
        using var host = new Host();
        var work = host.Work.Gate();
        var plan = host.Plans.Gate();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);

        await host.RefreshAsync("sub-1");
        await host.RefreshAsync("sub-1");
        work.SetResult(Ready("Alpha"));
        plan.SetResult(Plan(("Write the test", "pending")));
        await host.IdleAsync("sub-1");

        await Assert.That(host.Work.Requested.Length).IsEqualTo(2);
        await Assert.That(host.Plans.Requested.Length).IsEqualTo(2);
    }

    [Test]
    public async Task A_read_finishing_after_unsubscribe_sends_nothing() {
        using var host = new Host();
        var work = host.Work.Gate();
        var plan = host.Plans.Gate();
        await host.InitializeAsync();
        await host.SubscribeAsync("ctx-1", Session);
        var late = host.Context.PendingReadsForTesting("ctx-1");

        var reply = await host.CallAsync("context/unsubscribe", Fixture("context.unsubscribe.request.json")["params"]);
        work.SetResult(Ready("Alpha"));
        plan.SetResult(Plan(("Write the test", "pending")));
        await late.WithinDeadlineAsync("the reads that finish after the unsubscribe");
        var seen = await host.SettleAsync();

        await Assert.That(reply["result"]!.ToJsonString()).IsEqualTo("{}");
        await Assert.That(Changes(seen, "ctx-1")).IsEmpty();
    }

    /// <summary>The old subscription's reads are still on the wire when the client subscribes again under the same id, and
    /// finish only after the new subscription's snapshot.</summary>
    [Test]
    public async Task Resubscribing_with_the_same_id_drops_the_old_subscription_s_results() {
        using var host = new Host();
        var oldWork = host.Work.Gate();
        var oldPlan = host.Plans.Gate();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        var late = host.Context.PendingReadsForTesting("sub-1");
        await host.ReadsRequestedAsync(1);
        var newWork = host.Work.Gate();
        var newPlan = host.Plans.Gate();

        var again = await host.SubscribeAsync("sub-1", OtherSession);
        oldWork.SetResult(Ready("Old item"));
        oldPlan.SetResult(Plan(("Old task", "pending")));
        await late.WithinDeadlineAsync("the old subscription's reads");
        var afterLate = Changes(await host.SettleAsync(), "sub-1");
        newWork.SetResult(Ready("New item"));
        newPlan.SetResult(Plan(("New task", "pending")));
        await host.IdleAsync("sub-1");
        var afterNew = Changes(await host.SettleAsync(), "sub-1");

        await Assert.That(host.Work.Requested).IsEquivalentTo([Session, OtherSession]);
        await Assert.That(Text(again["params"], "session_id")).IsEqualTo(OtherSession);
        await Assert.That(afterLate).IsEmpty();
        await Assert.That(Text(Latest(again, afterNew, "work"), "title")).IsEqualTo("New item");
        await Assert.That(Text(Latest(again, afterNew, "plan")["tasks"]![0], "title")).IsEqualTo("New task");
    }

    /// <summary>A second subscription to the session shows the first one's content at once, from the sources' caches. Both
    /// reads go out under one credential, so once either signs out a later subscription starts from nothing in both. The
    /// other section's read cannot reach the server, which leaves its cache as it was: only the sign-out can clear it.</summary>
    [Test]
    [Arguments("work", "phase")]
    [Arguments("plan", "state")]
    public async Task A_signed_out_read_is_not_followed_by_cached_content(string signsOut, string member) {
        using var host = new Host();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        var cached = await host.SubscribeAsync("sub-2", Session);
        await host.IdleAsync("sub-2");

        host.Work.Answer(WorkContextRead.Of(signsOut == "work" ? WorkContextReadKind.SignedOut : WorkContextReadKind.Unreachable));
        host.Plans.Answer(SessionPlansRead.Of(signsOut == "plan" ? SessionPlansReadKind.SignedOut : SessionPlansReadKind.Unreachable));
        await host.RefreshAsync("sub-1");
        await host.IdleAsync("sub-1");
        var signedOut = Changes(await host.SettleAsync(), "sub-1");
        host.Work.Gate();
        host.Plans.Gate();
        var after = await host.SubscribeAsync("sub-3", Session);

        await Assert.That(Text(cached["params"]!["work"], "title")).IsEqualTo("Alpha");
        await Assert.That(Text(cached["params"]!["plan"], "state")).IsEqualTo("ready");
        await Assert.That(Text(signedOut.Select(m => m["params"]![signsOut]).Last(s => s is not null), member)).IsEqualTo("signed_out");
        await Assert.That(after["params"]!["work"]!.ToJsonString()).IsEqualTo(LoadingWork);
        await Assert.That(after["params"]!["plan"]!.ToJsonString()).IsEqualTo(LoadingPlan);
    }

    [Test]
    public async Task A_throwing_plan_source_marks_the_plan_unavailable_and_keeps_the_work_section() {
        using var host = new Host();
        host.Plans.Throw(new InvalidOperationException("plan read failed"));
        await host.InitializeAsync();
        var snapshot = await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        var first = Changes(await host.SettleAsync(), "sub-1");

        host.Plans.Throw(new InvalidOperationException("plan read failed again"));
        await host.RefreshAsync("sub-1");
        await host.IdleAsync("sub-1");
        var again = Changes(await host.SettleAsync(), "sub-1");

        await Assert.That(Text(Latest(snapshot, first, "work"), "phase")).IsEqualTo("ready");
        await Assert.That(Text(Latest(snapshot, first, "work"), "title")).IsEqualTo("Alpha");
        await Assert.That(Latest(snapshot, first, "plan").ToJsonString()).IsEqualTo("""{"state":"unavailable","tasks":[],"done":0,"total":0}""");
        await Assert.That(again).IsEmpty();
    }

    [Test]
    public async Task A_failing_plan_read_after_a_ready_plan_keeps_the_plan() {
        using var host = new Host();
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        await host.SettleAsync();

        host.Plans.Answer(SessionPlansRead.Of(SessionPlansReadKind.Unreachable));
        await host.RefreshAsync("sub-1");
        await host.IdleAsync("sub-1");
        var unreachable = Changes(await host.SettleAsync(), "sub-1");
        host.Plans.Throw(new InvalidOperationException("plan read failed"));
        await host.RefreshAsync("sub-1");
        await host.IdleAsync("sub-1");
        var thrown = Changes(await host.SettleAsync(), "sub-1");

        await Assert.That(host.Plans.Requested.Length).IsEqualTo(3);
        await Assert.That(unreachable).IsEmpty();
        await Assert.That(thrown).IsEmpty();
    }

    /// <summary>Any plan read that answered, with or without a plan, outranks a later one that could not reach the server.</summary>
    [Test]
    [Arguments(SessionPlansReadKind.Ready, "none")]
    [Arguments(SessionPlansReadKind.Unavailable, "none")]
    [Arguments(SessionPlansReadKind.SignedOut, "signed_out")]
    public async Task A_failing_plan_read_after_any_answered_one_keeps_the_plan(SessionPlansReadKind answered, string state) {
        using var host = new Host();
        host.Plans.Answer(SessionPlansRead.Of(answered));
        await host.InitializeAsync();
        var snapshot = await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        var first = Changes(await host.SettleAsync(), "sub-1");

        host.Plans.Answer(SessionPlansRead.Of(SessionPlansReadKind.Unreachable));
        await host.RefreshAsync("sub-1");
        await host.IdleAsync("sub-1");
        var unreachable = Changes(await host.SettleAsync(), "sub-1");
        host.Plans.Throw(new InvalidOperationException("plan read failed"));
        await host.RefreshAsync("sub-1");
        await host.IdleAsync("sub-1");
        var thrown = Changes(await host.SettleAsync(), "sub-1");

        await Assert.That(Text(Latest(snapshot, first, "plan"), "state")).IsEqualTo(state);
        await Assert.That(host.Plans.Requested.Length).IsEqualTo(3);
        await Assert.That(unreachable).IsEmpty();
        await Assert.That(thrown).IsEmpty();
    }

    [Test]
    public async Task An_unreachable_first_plan_read_marks_the_plan_unavailable() {
        using var host = new Host();
        host.Plans.Answer(SessionPlansRead.Of(SessionPlansReadKind.Unreachable));
        await host.InitializeAsync();
        var snapshot = await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        var changes = Changes(await host.SettleAsync(), "sub-1");

        await Assert.That(Latest(snapshot, changes, "plan").ToJsonString()).IsEqualTo("""{"state":"unavailable","tasks":[],"done":0,"total":0}""");
        await Assert.That(Text(Latest(snapshot, changes, "work"), "title")).IsEqualTo("Alpha");
    }

    [Test]
    public async Task No_work_item_and_a_plan_sends_phase_no_work_item_and_a_ready_plan() {
        using var host = new Host();
        host.Work.Default  = NoWorkItem;
        host.Plans.Default = Plan(("Write the test", "completed"), ("Drop the flag", "skipped"), ("Make it pass", "in_progress"), ("Document it", "pending"));
        await host.InitializeAsync();
        var snapshot = await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        var changes = Changes(await host.SettleAsync(), "sub-1");

        await Assert.That(Latest(snapshot, changes, "work").ToJsonString()).IsEqualTo(
            """{"phase":"no_work_item","stale":false,"title":"","blocked_by":[],"cycle":"none","parts":[],"issues":[],"people":[],"session_count":0}""");
        await Assert.That(Latest(snapshot, changes, "plan").ToJsonString()).IsEqualTo(
            """{"state":"ready","tasks":[""" +
            """{"id":"t1","title":"Write the test","status":"completed"},{"id":"t2","title":"Drop the flag","status":"skipped"},""" +
            """{"id":"t3","title":"Make it pass","status":"in_progress"},{"id":"t4","title":"Document it","status":"pending"}""" +
            """],"done":2,"total":4}""");
    }

    [Test]
    public async Task A_session_id_with_dashes_is_read_without_them() {
        using var host = new Host();
        await host.InitializeAsync();

        var snapshot = await host.SubscribeAsync("sub-1", "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
        await host.IdleAsync("sub-1");

        await Assert.That(Text(snapshot["params"], "session_id")).IsEqualTo(Session);
        await Assert.That(host.Work.Requested).IsEquivalentTo([Session]);
        await Assert.That(host.Plans.Requested).IsEquivalentTo([Session]);
    }

    [Test]
    [Arguments(null)]
    [Arguments("""{"subscription_id":"sub-1"}""")]
    [Arguments("""{"subscription_id":"sub-1","session_id":""}""")]
    [Arguments("""{"subscription_id":"sub-1","session_id":"  "}""")]
    [Arguments("""{"subscription_id":"sub-1","session_id":1}""")]
    [Arguments("""{"session_id":"0f1e2d3c4b5a69788796a5b4c3d2e1f0"}""")]
    public async Task A_missing_session_id_is_invalid_params(string? parameters) {
        using var host = new Host();
        await host.InitializeAsync();

        var reply = await host.CallAsync("context/subscribe", parameters is null ? null : JsonNode.Parse(parameters));
        var seen  = await host.SettleAsync();

        await Assert.That(reply["error"]!["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.InvalidParams);
        await Assert.That(seen.Where(m => Method(m) == "context/snapshot")).IsEmpty();
        await Assert.That(host.Work.Requested).IsEmpty();
    }

    [Test]
    public async Task Subscribe_before_initialize_is_not_initialized() {
        using var host = new Host();

        var reply = await host.CallAsync("context/subscribe", Fixture("context.subscribe.request.json")["params"]);

        await Assert.That(reply["error"]!["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.NotInitialized);
        await Assert.That(host.Seen).IsEmpty();
        await Assert.That(host.Work.Requested).IsEmpty();
    }

    [Test]
    public async Task The_lane_reconnecting_refreshes_every_subscription() {
        using var host = new Host();
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        await host.SubscribeAsync("sub-2", OtherSession);
        await host.IdleAsync("sub-1");
        await host.IdleAsync("sub-2");

        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Retrying, "connection refused"));
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await host.IdleAsync("sub-1");
        await host.IdleAsync("sub-2");

        await Assert.That(host.Work.Requested.Order()).IsEquivalentTo([OtherSession, OtherSession, Session, Session]);
        await Assert.That(host.Plans.Requested.Order()).IsEquivalentTo([OtherSession, OtherSession, Session, Session]);
    }

    [Test]
    public async Task The_same_account_reconnecting_keeps_the_content_and_only_refreshes() {
        using var host = new Host();
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "user-a"));
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        await host.SettleAsync();

        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Retrying, "connection refused"));
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "user-a"));
        await host.IdleAsync("sub-1");
        var changes = Changes(await host.SettleAsync(), "sub-1");

        await Assert.That(host.Work.Requested).IsEquivalentTo([Session, Session]);
        await Assert.That(host.Plans.Requested).IsEquivalentTo([Session, Session]);
        await Assert.That(changes).IsEmpty();
        await Assert.That(host.Work.Cached(Session)).IsNotNull();
        await Assert.That(host.Plans.Cached(Session)).IsNotNull();
    }

    /// <summary>The previous account's reads are still on the wire when the lane connects as another account: they settle
    /// into nothing, and both sections go back to loading before the new account's content arrives.</summary>
    [Test]
    public async Task An_account_change_restarts_a_subscription_from_loading() {
        using var host = new Host();
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "user-a"));
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        await host.SettleAsync();
        var oldWork = host.Work.Gate();
        var oldPlan = host.Plans.Gate();
        await host.RefreshAsync("sub-1");
        await host.ReadsRequestedAsync(2);
        var late = host.Context.PendingReadsForTesting("sub-1");

        host.Work.Default  = Ready("Beta");
        host.Plans.Default = Plan(("Review the change", "pending"));
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Retrying, "connection refused"));
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "user-b"));
        await host.IdleAsync("sub-1");
        oldWork.SetResult(Ready("Alpha"));
        oldPlan.SetResult(Plan(("Write the test", "completed"), ("Make it pass", "in_progress")));
        await late.WithinDeadlineAsync("the previous account's reads");
        var changes = Changes(await host.SettleAsync(), "sub-1");

        await Assert.That(WorkSteps(changes)).IsEqualTo("loading: | ready:Beta");
        await Assert.That(PlanSteps(changes)).IsEqualTo("loading: | ready:Review the change");
        await Assert.That(changes[0]["params"]!["work"]!.ToJsonString()).IsEqualTo(LoadingWork);
        await Assert.That(changes[0]["params"]!["plan"]!.ToJsonString()).IsEqualTo(LoadingPlan);
    }

    /// <summary>Reads after the sign-out cannot reach the server, so nothing replaces what the previous account was shown
    /// but the restart itself.</summary>
    [Test]
    public async Task A_lane_sign_out_clears_every_open_subscription() {
        using var host = new Host();
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "user-a"));
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        await host.SubscribeAsync("sub-2", OtherSession);
        await host.IdleAsync("sub-1");
        await host.IdleAsync("sub-2");
        await host.SettleAsync();

        host.Work.Default  = WorkContextRead.Of(WorkContextReadKind.Unreachable, "connection refused");
        host.Plans.Default = SessionPlansRead.Of(SessionPlansReadKind.Unreachable);
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.SignedOut));
        await host.IdleAsync("sub-1");
        await host.IdleAsync("sub-2");
        var seen = await host.SettleAsync();

        foreach (var id in new[] { "sub-1", "sub-2" }) {
            await Assert.That(WorkSteps(Changes(seen, id))).IsEqualTo("loading: | unreachable:");
            await Assert.That(PlanSteps(Changes(seen, id))).IsEqualTo("loading: | unavailable:");
        }
        await Assert.That(host.Work.Cached(Session)).IsNull();
        await Assert.That(host.Plans.Cached(Session)).IsNull();
    }

    [Test]
    public async Task A_subscription_after_an_account_change_is_not_seeded_from_the_previous_account() {
        using var host = new Host();
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "user-a"));
        await host.InitializeAsync();
        await host.SubscribeAsync("sub-1", Session);
        await host.IdleAsync("sub-1");
        await host.CallAsync("context/unsubscribe", new JsonObject { ["subscription_id"] = "sub-1" });
        var held = (Work: host.Work.Cached(Session) is not null, Plan: host.Plans.Cached(Session) is not null);

        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Retrying, "connection refused"));
        host.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "user-b"));
        host.Work.Gate();
        host.Plans.Gate();
        var after = await host.SubscribeAsync("sub-2", Session);

        await Assert.That(held).IsEqualTo((true, true));
        await Assert.That(after["params"]!["work"]!.ToJsonString()).IsEqualTo(LoadingWork);
        await Assert.That(after["params"]!["plan"]!.ToJsonString()).IsEqualTo(LoadingPlan);
    }

    /// <summary>Each work section the changes carried, in order, as its phase and title.</summary>
    static string WorkSteps(IEnumerable<JsonObject> changes) =>
        string.Join(" | ", changes.Select(m => m["params"]!["work"]).OfType<JsonNode>().Select(w => $"{Text(w, "phase")}:{Text(w, "title")}"));

    /// <summary>Each plan section the changes carried, in order, as its state and task titles.</summary>
    static string PlanSteps(IEnumerable<JsonObject> changes) =>
        string.Join(" | ", changes.Select(m => m["params"]!["plan"]).OfType<JsonNode>()
            .Select(p => $"{Text(p, "state")}:{string.Join(",", p["tasks"]!.AsArray().Select(t => Text(t, "title")))}"));
}
