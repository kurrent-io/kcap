using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Recording.Tests.Unit;

/// <summary>A synthesised end goes the way a hook's goes: spooled on any status a retry can repair, a lapsed credential
/// included, and dropped only when the server rejects the payload.</summary>
public class SessionEndPosterTests {
    const string Session = "0123456789abcdef0123456789abcdef";
    const string Url     = "http://kcap.test";

    [TempDir] public required TempDir Tmp { get; init; }

    ConfigRoot Config => field ??= new(Tmp.CreateDir("config"));

    readonly List<(string Session, string Vendor)> _whatsDone = [];

    SessionEndPoster Poster(HttpStatusCode status, string body = "") =>
        new(Config, Url, new AnsweringHttp(status, body), TimeProvider.System, _ => { }, (session, vendor) => _whatsDone.Add((session, vendor)));

    static JsonObject End() => SessionEnd.ParentExitEndBody(Session, "/t.jsonl", "/repo", "parent_exited", DateTimeOffset.UtcNow, HostedAgent.Terminal);

    bool Spooled => new HookSpool(Config, TimeProvider.System, Url).HasBacklog(Session);

    [Test]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    public async Task A_status_a_retry_repairs_spools_the_end(HttpStatusCode status) {
        await Assert.That(await Poster(status).PostAsync(Session, "claude", End(), null, "Process-exit")).IsTrue();

        await Assert.That(Spooled).IsTrue();
    }

    [Test]
    public async Task A_rejected_end_is_dropped() {
        await Assert.That(await Poster(HttpStatusCode.BadRequest).PostAsync(Session, "claude", End(), null, "Process-exit")).IsFalse();

        await Assert.That(Spooled).IsFalse();
    }

    [Test]
    public async Task An_accepted_end_starts_the_summary_the_server_asks_for() {
        await Assert.That(await Poster(HttpStatusCode.OK, """{"generate_whats_done":true}""").PostAsync(Session, "claude", End(), null, "Process-exit")).IsTrue();

        await Assert.That(_whatsDone).IsEquivalentTo([(Session, "claude")]);
        await Assert.That(Spooled).IsFalse();
    }

    /// <summary>A start still in the spool reaches the server first.</summary>
    [Test]
    public async Task An_end_queues_behind_the_sessions_spooled_hooks() {
        new HookSpool(Config, TimeProvider.System, Url).Append(Session, "session-start", "{}");

        await Assert.That(await Poster(HttpStatusCode.OK).PostAsync(Session, "claude", End(), null, "Process-exit")).IsTrue();

        await Assert.That(_whatsDone).IsEmpty();
    }

    sealed class AnsweringHttp(HttpStatusCode status, string body) : ICapacitorHttpClient {
        HttpClient Client() => new(new Answering(status, body));

        public Task<HttpClient> ForCommandAsync(CancellationToken ct = default) => Task.FromResult(Client());
        public Task<HttpClient> ForSessionAsync(CancellationToken ct = default) => Task.FromResult(Client());
        public Task<AuthAttempt> ForHookAsync(CancellationToken ct = default) => Task.FromResult(new AuthAttempt(Client(), AuthStatus.Ok, null, null));
        public Task<AuthAttempt> ForWaitAsync(CancellationToken ct = default) => Task.FromResult(new AuthAttempt(Client(), AuthStatus.Ok, null, null));
        public Task<AuthAttempt> ForProtectedReadAsync(CancellationToken ct = default) => Task.FromResult(new AuthAttempt(Client(), AuthStatus.Ok, null, null));
        public Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default) => Task.FromResult(Client());
        public Task<HttpClient> ForMemoryAsync(CancellationToken ct = default) => Task.FromResult(Client());
        public HttpClient Anonymous() => Client();
        public HttpClient Loopback() => Client();
        public HttpClient Bearer() => Client();
    }

    sealed class Answering(HttpStatusCode status, string body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
