using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.RepoInstructions;
using Capacitor.Cli.Recording.RepoInstructions;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Recording.Tests.Unit.RepoInstructions;

public class RepoInstructionsUploaderTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string Session = "0199a3b2-1c2d-7e8f-9a0b-1c2d3e4f5a6b";

    static readonly RepositoryPayload Repo = new() { Host = "github.com", Owner = "acme", RepoName = "widgets" };

    static readonly string Hash = RepoHashHelper.ComputeRepoHash("acme", "widgets");

    static readonly RepoInstructionFile Claude = new("CLAUDE.md", "abc", "rules");

    static readonly RepoInstructionSet Set = new([Claude], RepoInstructionsDigest.Of([Claude]), []);

    readonly WireMockServer _server = WireMockServer.Start();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    readonly List<string> _log = [];
    int _reads;

    public void Dispose() => _server.Dispose();

    RepoInstructionsDigestCache Cache => RepoInstructionsDigestCache.Default(Config.Root);

    void Answer(int status, string body = "{}", string? retryAfter = null) {
        var response = Response.Create().WithStatusCode(status).WithBody(body);
        if (retryAfter is not null) response = response.WithHeader("Retry-After", retryAfter);
        _server.Given(Request.Create().WithPath("/api/repos/instructions").UsingPost()).RespondWith(response);
    }

    int Posts => _server.LogEntries.Count;

    RepoInstructionsUploader Uploader(Profile? profile = null) => new(Session, _server.Url!, Config.Root, () => profile, new FixedCapacitorHttpClient(), Cache,
        _time, _log.Add, read: (_, _, _) => {
            _reads++;
            return Task.FromResult(RepoInstructionsRead.Of(Set));
        });

    Task TickAsync(RepoInstructionsUploader uploader, RepositoryPayload? repo = null) => uploader.TickAsync(repo ?? Repo, Tmp.Path, () => { }, CancellationToken.None);

    [Test]
    public async Task A_stored_upload_is_cached_so_the_next_session_sends_nothing() {
        Answer(200, """{"status":"stored"}""");

        await TickAsync(Uploader());
        var next = Uploader();
        await TickAsync(next);

        await Assert.That(Posts).IsEqualTo(1);
        using var body = JsonDocument.Parse(_server.LogEntries.Single().RequestMessage.Body!);
        await Assert.That(body.RootElement.GetProperty("session_id").GetString()).IsEqualTo(Session);
        await Assert.That(body.RootElement.GetProperty("digest").GetString()).IsEqualTo(Set.Digest);
        await Assert.That(body.RootElement.GetProperty("files")[0].GetProperty("path").GetString()).IsEqualTo("CLAUDE.md");
        await Assert.That(next.Done).IsTrue();
        await Assert.That(_log[^1]).Contains("unchanged");
    }

    [Test]
    public async Task A_server_that_does_not_know_the_repository_is_asked_again_until_the_window_closes() {
        Answer(503);
        var uploader = Uploader();

        await TickAsync(uploader);
        await TickAsync(uploader);
        await Assert.That(Posts).IsEqualTo(1);

        while (!uploader.Done) {
            _time.Advance(RepoInstructionsUploader.MaxBackoff);
            await TickAsync(uploader);
        }

        await Assert.That(Posts).IsGreaterThan(2);
        await Assert.That(_time.GetUtcNow() - new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero)).IsGreaterThanOrEqualTo(RepoInstructionsUploader.UnknownRepositoryWindow);
        await Assert.That(Cache.Read(_server.Url!, Hash)).IsNull();
        await Assert.That(_reads).IsEqualTo(1);
        await Assert.That(_log).HasSingleItem();
    }

    [Test]
    [Arguments("30")]
    [Arguments("Thu, 26 Nov 2026 12:00:30 GMT")]
    public async Task A_rate_limit_is_waited_out_once(string retryAfter) {
        Answer(429, retryAfter: retryAfter);
        var uploader = Uploader();

        await TickAsync(uploader);
        _time.Advance(TimeSpan.FromSeconds(29));
        await TickAsync(uploader);
        await Assert.That(Posts).IsEqualTo(1);

        _time.Advance(TimeSpan.FromSeconds(1));
        await TickAsync(uploader);

        await Assert.That(Posts).IsEqualTo(2);
        await Assert.That(uploader.Done).IsTrue();
    }

    [Test]
    public async Task A_refused_upload_is_logged_and_leaves_the_cache_alone() {
        Answer(413, """{"title":"too_large","detail":"The files total more than 196608 bytes."}""");
        var uploader = Uploader();

        await TickAsync(uploader);

        await Assert.That(uploader.Done).IsTrue();
        await Assert.That(_log.Single()).Contains("HTTP 413 too_large: The files total more than 196608 bytes.");
        await Assert.That(Cache.Read(_server.Url!, Hash)).IsNull();
    }

    [Test]
    public async Task The_profile_opt_out_reads_and_sends_nothing() {
        Answer(200);
        var uploader = Uploader(new Profile { DisableRepoInstructionsUpload = true });

        await TickAsync(uploader);

        await Assert.That(uploader.Done).IsTrue();
        await Assert.That(_reads).IsEqualTo(0);
        await Assert.That(Posts).IsEqualTo(0);
    }

    [Test]
    public async Task Nothing_happens_before_the_repository_is_detected() {
        Answer(200);
        var uploader = Uploader();

        await uploader.TickAsync(null, Tmp.Path, () => { }, CancellationToken.None);

        await Assert.That(uploader.Done).IsFalse();
        await Assert.That(_reads).IsEqualTo(0);
    }
}
