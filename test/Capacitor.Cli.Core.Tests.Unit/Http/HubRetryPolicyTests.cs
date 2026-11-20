using Capacitor.Cli.Core.Http;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

public class HubRetryPolicyTests {
    static RetryContext After(long previous, Exception? reason = null) =>
        new() { PreviousRetryCount = previous, RetryReason = reason ?? new HttpRequestException("refused") };

    /// <summary>The ladder all three hub clients share, and that it never answers null: a null closes the
    /// connection, and each client would then recover on a schedule of its own.</summary>
    [Test]
    [Arguments(0L, 1)]
    [Arguments(1L, 2)]
    [Arguments(2L, 5)]
    [Arguments(3L, 10)]
    [Arguments(4L, 30)]
    [Arguments(5L, 30)]
    [Arguments(10_000L, 30)]
    public async Task Each_attempt_waits_its_step_and_the_last_step_repeats(long previous, int seconds) {
        await Assert.That(new HubRetryPolicy().NextRetryDelay(After(previous))).IsEqualTo(TimeSpan.FromSeconds(seconds));
    }

    [Test]
    public async Task A_failure_the_owner_names_closes_the_connection() {
        var policy = new HubRetryPolicy(ex => ex is UnauthorizedAccessException);

        await Assert.That(policy.NextRetryDelay(After(0, new UnauthorizedAccessException()))).IsNull();
        await Assert.That(policy.NextRetryDelay(After(7))).IsEqualTo(TimeSpan.FromSeconds(30));
    }
}
