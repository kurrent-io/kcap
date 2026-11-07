using System.Collections.Concurrent;
using System.Net;
using Capacitor.Cli.Commands;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class TranscriptImportLedgerTests {
    const string Server  = "http://ledger.test";
    const string Late    = "late";
    const string Stalled = "stalled";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>Both sessions are hidden on the first read; on the retry one answers and the other never does. The
    /// confirmation must end when its budget runs out, keeping the mark it did take, not wait on the stalled request.</summary>
    [Test]
    public async Task A_stalled_probe_ends_the_confirmation_when_its_budget_runs_out() {
        var handler = new HidingThenStallingHandler();
        using var http = new HttpClient(handler);
        var time   = new FakeTimeProvider();
        var ledger = TranscriptImportLedger.ForRecording(Config.Root);

        await ledger.RecordAsync(http, Server, Late, 6, CancellationToken.None);
        await ledger.RecordAsync(http, Server, Stalled, 6, CancellationToken.None);

        var confirming = ledger.ConfirmAsync(http, time, CancellationToken.None);
        await handler.Stalling.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(30));
        await confirming.WaitAsync(TimeSpan.FromSeconds(10));

        ledger.Save();
        var saved = TranscriptImportLedger.Load(Config.Root);
        await Assert.That(saved.ResumeFrom(Server, Late, 3)).IsEqualTo(6);
        await Assert.That(saved.ResumeFrom(Server, Stalled, 3)).IsNull();
    }

    sealed class HidingThenStallingHandler : HttpMessageHandler {
        readonly ConcurrentDictionary<string, int> _reads = new();

        public TaskCompletionSource Stalling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var session = request.RequestUri!.Segments[^2].TrimEnd('/');
            if (_reads.AddOrUpdate(session, 1, (_, n) => n + 1) == 1) return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (session == Stalled) {
                Stalling.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"last_line_number":3}""") };
        }
    }
}
