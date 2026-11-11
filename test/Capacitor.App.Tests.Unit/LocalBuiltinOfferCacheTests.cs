using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Tests.Unit;

public class LocalBuiltinOfferCacheTests {
    static readonly LocalEmbeddingModelManifest Offered = new("builtin-test", 1, new('e', 64), [new("model.onnx", new('f', 64), 5)]);

    [Test]
    public async Task Lookups_of_one_version_share_one_fetch() {
        var calls = new List<string>();
        var cache = new LocalBuiltinOfferCache(version => {
            lock (calls) calls.Add(version);
            return Task.FromResult<LocalEmbeddingModelManifest?>(Offered);
        });

        await cache.LookupAsync("1.4.1", CancellationToken.None);
        await cache.LookupAsync("1.4.1", CancellationToken.None);
        await cache.LookupAsync("1.5.0", CancellationToken.None);

        await Assert.That(calls).IsEquivalentTo(["1.4.1", "1.5.0"]);
    }

    [Test]
    public async Task A_lookup_still_running_is_shared() {
        var calls = 0;
        var answer = new TaskCompletionSource<LocalEmbeddingModelManifest?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new LocalBuiltinOfferCache(_ => {
            Interlocked.Increment(ref calls);
            return answer.Task;
        });

        var first  = cache.LookupAsync("1.4.1", CancellationToken.None);
        var second = cache.LookupAsync("1.4.1", CancellationToken.None);
        answer.SetResult(Offered);

        await Assert.That(await first).IsEqualTo(Offered);
        await Assert.That(await second).IsEqualTo(Offered);
        await Assert.That(calls).IsEqualTo(1);
    }

    /// A lapse in the network reads as no offer; keeping it would hide the built-in model until the app restarts.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_failed_or_empty_lookup_is_asked_again(bool throws) {
        var calls = 0;
        var cache = new LocalBuiltinOfferCache(_ => ++calls == 1
            ? throws ? Task.FromException<LocalEmbeddingModelManifest?>(new HttpRequestException("down")) : Task.FromResult<LocalEmbeddingModelManifest?>(null)
            : Task.FromResult<LocalEmbeddingModelManifest?>(Offered));

        if (throws) await Assert.ThrowsAsync<HttpRequestException>(async () => await cache.LookupAsync("1.4.1", CancellationToken.None));
        else await Assert.That(await cache.LookupAsync("1.4.1", CancellationToken.None)).IsNull();

        await Assert.That(await cache.LookupAsync("1.4.1", CancellationToken.None)).IsEqualTo(Offered);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task A_cancelled_caller_leaves_the_shared_lookup_running() {
        var calls = 0;
        var answer = new TaskCompletionSource<LocalEmbeddingModelManifest?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new LocalBuiltinOfferCache(_ => {
            Interlocked.Increment(ref calls);
            return answer.Task;
        });
        using var cts = new CancellationTokenSource();

        var abandoned = cache.LookupAsync("1.4.1", cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await abandoned);

        var waiting = cache.LookupAsync("1.4.1", CancellationToken.None);
        answer.SetResult(Offered);

        await Assert.That(await waiting).IsEqualTo(Offered);
        await Assert.That(calls).IsEqualTo(1);
    }
}
