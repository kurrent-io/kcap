using Capacitor.App.Services;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.Services;

public class SharedSessionDetailReaderTests {
    sealed class Server {
        public readonly List<string> Requests = [];
        public readonly List<CancellationToken> Tokens = [];
        public TaskCompletionSource<SessionDetailFetch> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SessionDetailFetch> Read(string sessionId, CancellationToken ct) {
            Task<SessionDetailFetch> answer;
            lock (Requests) { Requests.Add(sessionId); Tokens.Add(ct); answer = Answer.Task; }
            return await answer.WaitAsync(ct);
        }

        public int Count { get { lock (Requests) return Requests.Count; } }
    }

    [Test]
    public async Task Two_concurrent_reads_for_one_session_issue_one_request() {
        var server = new Server();
        var shared = new SharedSessionDetailReader(server.Read);
        var first = shared.ReadAsync("s1", CancellationToken.None);
        var second = shared.ReadAsync("s1", CancellationToken.None);
        await WaitUntilAsync(() => server.Count == 1, what: "request issued");
        var body = new SessionDetailFetch(RemoteFixtures.Detail());
        server.Answer.SetResult(body);

        await Assert.That(await first).IsSameReferenceAs(body);
        await Assert.That(await second).IsSameReferenceAs(body);
        await Assert.That(server.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Reads_for_different_sessions_and_reads_after_one_settled_each_issue_their_own() {
        var server = new Server();
        server.Answer.SetResult(new SessionDetailFetch(RemoteFixtures.Detail()));
        var shared = new SharedSessionDetailReader(server.Read);
        await Task.WhenAll(shared.ReadAsync("s1", CancellationToken.None), shared.ReadAsync("s2", CancellationToken.None));
        await shared.ReadAsync("s1", CancellationToken.None);

        await Assert.That(server.Requests).IsEquivalentTo(new[] { "s1", "s2", "s1" });
    }

    /// One caller leaving must not cancel the request another is still waiting on; the last one leaving does.
    [Test]
    public async Task The_request_is_cancelled_only_when_every_caller_has_gone() {
        var server = new Server();
        var shared = new SharedSessionDetailReader(server.Read);
        using var leaving = new CancellationTokenSource();
        using var staying = new CancellationTokenSource();
        var left = shared.ReadAsync("s1", leaving.Token);
        var stays = shared.ReadAsync("s1", staying.Token);
        await WaitUntilAsync(() => server.Count == 1, what: "request issued");

        await leaving.CancelAsync();
        await Assert.That(async () => await left).Throws<OperationCanceledException>();
        await Assert.That(server.Tokens[0].IsCancellationRequested).IsFalse();

        await staying.CancelAsync();
        await Assert.That(async () => await stays).Throws<OperationCanceledException>();
        await WaitUntilAsync(() => server.Tokens[0].IsCancellationRequested, what: "abandoned request cancelled");

        server.Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Answer.SetResult(new SessionDetailFetch(null, NotFound: true));
        await Assert.That((await shared.ReadAsync("s1", CancellationToken.None)).NotFound).IsTrue();
        await Assert.That(server.Count).IsEqualTo(2);
    }

    /// The read a caller joined may have gone out with a token refreshed since, or been answered
    /// before the session existed; only the caller that started it takes either answer as final.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_joiner_handed_a_refusal_or_a_miss_reads_again_and_the_starter_does_not(bool refusal) {
        var server = new Server();
        var shared = new SharedSessionDetailReader(server.Read);
        var starter = shared.ReadAsync("s1", CancellationToken.None);
        await WaitUntilAsync(() => server.Count == 1, what: "request issued");
        var joiner = shared.ReadAsync("s1", CancellationToken.None);
        var first = server.Answer;
        server.Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Answer.SetResult(new SessionDetailFetch(RemoteFixtures.Detail()));
        first.SetResult(refusal ? new SessionDetailFetch(null, Unauthorized: true) : new SessionDetailFetch(null, NotFound: true));

        var started = await starter;
        await Assert.That(refusal ? started.Unauthorized : started.NotFound).IsTrue();
        await Assert.That((await joiner).Detail).IsNotNull();
        await Assert.That(server.Count).IsEqualTo(2);
    }
}
