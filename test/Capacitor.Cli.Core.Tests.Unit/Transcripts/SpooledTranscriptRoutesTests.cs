using System.Net;
using Capacitor.Cli.Core.Transcripts;

namespace Capacitor.Cli.Core.Tests.Unit.Transcripts;

public class SpooledTranscriptRoutesTests {
    const string Session = "5cdc4fcc-ff9e-4fdc-8d93-cf47ff5a140a";

    [TempDir] public required TempDir Tmp { get; init; }

    static string User(int n) =>
        $$$"""{"type":"user","uuid":"10000000-0000-4000-8000-{{{n:D12}}}","parentUuid":null,"timestamp":"2026-11-16T08:00:{{{n:D2}}}Z","message":{"role":"user","content":"line {{{n}}}"}}""";

    /// <summary>The same stream with a budget it can replay in, so the raw answer is the overrun's and nothing else's.</summary>
    [Test]
    public async Task A_stream_whose_replay_overruns_its_budget_goes_raw() {
        var path  = Tmp.CreateFile("t.jsonl", [User(0), User(1), User(2), User(3)]);
        var batch = new TranscriptBatch { SessionId = Session, Vendor = "claude", Lines = [User(3)], LineNumbers = [3], TranscriptPath = path };

        using var server = new Supporting();
        using var client = new HttpClient(server);

        var ample   = await new SpooledTranscriptRoutes(client, "http://s", TimeSpan.FromSeconds(30), TimeProvider.System).ForAsync(batch, CancellationToken.None);
        var overrun = await new SpooledTranscriptRoutes(client, "http://s", TimeSpan.Zero, TimeProvider.System).ForAsync(batch, CancellationToken.None);

        await Assert.That(ample).IsNotNull();
        await Assert.That(overrun).IsNull();
    }

    sealed class Supporting : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"canonical_format":1}""") });
    }
}
