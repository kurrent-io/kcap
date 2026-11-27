namespace Capacitor.Cli.Core.Tests.Unit;

public class LocalRepoCheckoutsTests {
    [Test]
    public async Task A_lookup_stops_waiting_when_its_token_is_cancelled() {
        var checkouts = new LocalRepoCheckouts(() => new TaskCompletionSource<string[]>().Task);
        using var cancelled = new CancellationTokenSource();
        var lookup = checkouts.FindAsync("0123456789abcdef", cancelled.Token);

        await cancelled.CancelAsync();

        await Assert.That(async () => await lookup.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System)).Throws<OperationCanceledException>();
    }
}
