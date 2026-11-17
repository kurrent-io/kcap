using System.Text.Json;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>Pins which failures a degrading client turns into "unreachable" and which propagate.</summary>
public class HttpReadFailuresTests {
    public static IEnumerable<Func<(Exception Error, bool Transient)>> Table() {
        yield return () => (new HttpRequestException("refused"), true);
        yield return () => (new JsonException("bad body"), true);
        yield return () => (new NotSupportedException("content type"), true);
        yield return () => (new IOException("reset"), true);
        yield return () => (new EndOfStreamException(), true);
        yield return () => (new InvalidOperationException("bug"), false);
        yield return () => (new ArgumentException("bug"), false);
        yield return () => (new ObjectDisposedException("client"), false);
        yield return () => (new AggregateException(new HttpRequestException("inner")), false);
    }

    [Test]
    [MethodDataSource(nameof(Table))]
    public async Task A_non_cancellation_failure_is_classified_by_its_own_type(Exception error, bool transient) {
        await Assert.That(HttpReadFailures.IsTransient(error, CancellationToken.None)).IsEqualTo(transient);
    }

    /// <summary>The token decides, not the exception: an HTTP failure stays transient after the caller
    /// cancels, because only the cancellation itself is the caller's.</summary>
    [Test]
    [MethodDataSource(nameof(Table))]
    public async Task A_cancelled_caller_does_not_change_a_non_cancellation_verdict(Exception error, bool transient) {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(HttpReadFailures.IsTransient(error, cts.Token)).IsEqualTo(transient);
    }

    [Test]
    public async Task A_timeout_with_the_caller_still_live_is_transient() {
        await Assert.That(HttpReadFailures.IsTransient(new TaskCanceledException("timeout", new TimeoutException()), CancellationToken.None)).IsTrue();
        await Assert.That(HttpReadFailures.IsTransient(new OperationCanceledException(), CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task The_callers_own_cancellation_is_not_transient() {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(HttpReadFailures.IsTransient(new OperationCanceledException(cts.Token), cts.Token)).IsFalse();
        await Assert.That(HttpReadFailures.IsTransient(new TaskCanceledException(), cts.Token)).IsFalse();
    }
}
