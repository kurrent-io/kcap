using System.Net;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>A post a drain sent and heard nothing back for may still be on the server's side, and only a record of that
/// attempt outlives the pass that sent it.</summary>
public class UnansweredPostsTests {
    const string Sid    = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Dashed = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Answering(Func<HttpResponseMessage> answer) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer());
    }

    HookSpool Lifecycle() {
        var spool = new HookSpool(Tmp.PathTo("spool"), TimeProvider.System);
        spool.Append(Dashed, "session-start/codex", $$"""{"session_id":"{{Dashed}}"}""");

        return spool;
    }

    async Task PassAsync(HookSpool lifecycle, Func<HttpResponseMessage> answer) {
        using var client = new HttpClient(new Answering(answer));

        await LifecycleSpoolDrain.RunAsync(
            Config.Root, new CursorMarkers(Config.Root, TimeProvider.System), client, "http://s", lifecycle, new TranscriptSpool(Tmp.PathTo("tx"), TimeProvider.System),
            currentSessionId: null, budget: TimeSpan.FromSeconds(30), time: TimeProvider.System, ct: CancellationToken.None);
    }

    /// <summary>The record is down while the post is out, so a process that dies mid-post leaves it. Any answer settles
    /// that attempt, an error status too: the server has finished with the request. Kept under the canonical id, which is
    /// the one <c>kcap disable</c> asks with.</summary>
    [Test]
    public async Task A_post_is_recorded_while_it_is_out_and_any_answer_settles_it() {
        var  lifecycle = Lifecycle();
        bool? recordedWhileOut = null;

        await PassAsync(lifecycle, () => {
            recordedWhileOut = UnansweredPosts.Has(lifecycle.DirectoryPath, Sid);

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        await Assert.That(recordedWhileOut).IsTrue();
        await Assert.That(UnansweredPosts.Has(lifecycle.DirectoryPath, Sid)).IsFalse();
        await Assert.That(lifecycle.HasBacklog(Dashed)).IsTrue();
    }

    /// <summary>A later post's answer is no answer to the one that timed out, which the server may still be taking in.</summary>
    [Test]
    public async Task A_post_that_times_out_stays_recorded_whatever_a_later_one_gets() {
        var lifecycle = Lifecycle();

        await PassAsync(lifecycle, () => throw new TaskCanceledException("The post timed out."));

        await Assert.That(UnansweredPosts.Has(lifecycle.DirectoryPath, Sid)).IsTrue();
        await Assert.That(lifecycle.HasBacklog(Dashed)).IsTrue();

        await PassAsync(lifecycle, () => new HttpResponseMessage(HttpStatusCode.OK));

        await Assert.That(lifecycle.HasBacklog(Dashed)).IsFalse();
        await Assert.That(UnansweredPosts.Has(lifecycle.DirectoryPath, Sid)).IsTrue();
    }

    /// <summary>A connection never made sent nothing, so the server has nothing of it to take in.</summary>
    [Test]
    public async Task A_post_that_never_left_records_nothing() {
        var lifecycle = Lifecycle();

        await PassAsync(lifecycle, () => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused."));

        await Assert.That(UnansweredPosts.Has(lifecycle.DirectoryPath, Sid)).IsFalse();
        await Assert.That(lifecycle.HasBacklog(Dashed)).IsTrue();
    }

    /// <summary>The hooks' own drain, through the posters that let a post with no answer through to it.</summary>
    [Test]
    public async Task A_hook_drain_post_with_no_answer_stays_recorded() {
        var lifecycle = Lifecycle();
        bool? recordedWhileOut = null;

        await lifecycle.DrainAllAsync(null, (_, _) => {
            recordedWhileOut = UnansweredPosts.Has(lifecycle.DirectoryPath, Sid);

            throw new TaskCanceledException("The post timed out.");
        }, TimeSpan.FromSeconds(30), CancellationToken.None);
        await lifecycle.DrainAllAsync(null, (_, _) => Task.FromResult(DrainOutcome.Delivered), TimeSpan.FromSeconds(30), CancellationToken.None);

        await Assert.That(recordedWhileOut).IsTrue();
        await Assert.That(UnansweredPosts.Has(lifecycle.DirectoryPath, Sid)).IsTrue();
    }
}
