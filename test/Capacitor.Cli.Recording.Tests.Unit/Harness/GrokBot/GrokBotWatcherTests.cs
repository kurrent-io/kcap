using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Harness.GrokBot;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.GrokBot;

/// <summary>Pins one run of the watcher end to end over a desktop chat store: the calls it makes and in
/// what order, that state moves only once the server accepts them, and what keeps two watchers apart.</summary>
public class GrokBotWatcherTests {
    const string Chat = "4d30ec7f-1027-4a23-86d7-d70f37e5bfec";

    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Server(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler {
        public List<(string Path, string Body)> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Posts.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(ct)));

            return new HttpResponseMessage(status) { Content = new StringContent("{}") };
        }
    }

    readonly List<string> _log = [];

    GrokBotWatcher Watcher(Server server, TimeProvider? time = null) => new(
        new ConfigRoot(Tmp.PathTo("config")), Resolutions.Of(new() { ServerUrl = "https://acme.kcap.ai/", DefaultVisibility = "org_public" }),
        new UserHome(Tmp.PathTo("home")), new FixedCapacitorHttpClient(background: server), time ?? TimeProvider.System);

    /// <summary>A store holding one chat of two entries, both far older than the gap.</summary>
    GrokBotWatchOptions OneOldChat(bool dryRun = false) {
        var entries = """{"kind":"message","id":"t0u","role":"user","content":"hi","timestampMs":1790595500000,"seq":1},"""
                    + """{"kind":"send-message","id":"t0s0","message":{"type":"text","content":"hello"},"timestampMs":1790595501000,"seq":2}""";

        Tmp.CreateFile(["chats", GrokBotChatFile.NameOfChat(Chat)],
            "{\"schemaVersion\":1,\"value\":{\"entries\":[" + entries + "]}}");

        return new GrokBotWatchOptions(Desktop: Tmp.PathTo("chats"), Once: true, DryRun: dryRun);
    }

    Task<GrokBotWatchOutcome> RunAsync(Server server, GrokBotWatchOptions options) => Watcher(server).RunAsync(options, _log.Add, CancellationToken.None);

    [Test]
    public async Task A_quiet_chat_is_opened_sent_and_ended_and_a_second_run_sends_nothing() {
        var server  = new Server();
        var options = OneOldChat();

        await Assert.That(await RunAsync(server, options)).IsEqualTo(GrokBotWatchOutcome.Completed);

        await Assert.That(server.Posts.Select(p => p.Path)).IsEquivalentTo(
            ["/hooks/session-start/grok-bot", "/hooks/transcript", "/hooks/session-end/grok-bot"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        using var start = JsonDocument.Parse(server.Posts[0].Body);
        await Assert.That(start.RootElement.GetProperty("session_id").GetString()).IsEqualTo(GrokBotSessionizer.SessionId(Chat, "t0u"));
        await Assert.That(start.RootElement.GetProperty("default_visibility").GetString()).IsEqualTo("private");
        await Assert.That(start.RootElement.GetProperty("started_at").GetDateTimeOffset()).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1790595500000));

        using var batch = JsonDocument.Parse(server.Posts[1].Body);
        await Assert.That(batch.RootElement.GetProperty("vendor").GetString()).IsEqualTo("grok-bot");
        await Assert.That(batch.RootElement.GetProperty("strict").GetBoolean()).IsTrue();
        await Assert.That(batch.RootElement.GetProperty("line_numbers").EnumerateArray().Select(n => n.GetInt32())).IsEquivalentTo([1, 2]);
        await Assert.That(batch.RootElement.GetProperty("lines").GetArrayLength()).IsEqualTo(2);

        using var line = JsonDocument.Parse(batch.RootElement.GetProperty("lines")[0].GetString()!);
        await Assert.That(line.RootElement.GetProperty("agentId").GetString()).IsEqualTo(Chat);

        using var end = JsonDocument.Parse(server.Posts[2].Body);
        await Assert.That(end.RootElement.GetProperty("ended_at").GetDateTimeOffset()).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1790595501000));

        await Assert.That(await RunAsync(server, options)).IsEqualTo(GrokBotWatchOutcome.Completed);
        await Assert.That(server.Posts.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_refused_call_fails_the_run_and_leaves_everything_to_send_again() {
        var refusing = new Server(HttpStatusCode.BadRequest);
        var options  = OneOldChat();

        await Assert.That(await RunAsync(refusing, options)).IsEqualTo(GrokBotWatchOutcome.Failed);
        await Assert.That(refusing.Posts.Count).IsEqualTo(1);
        await Assert.That(_log.Any(l => l.Contains("answered 400"))).IsTrue();

        var accepting = new Server();
        await Assert.That(await RunAsync(accepting, options)).IsEqualTo(GrokBotWatchOutcome.Completed);
        await Assert.That(accepting.Posts.Count).IsEqualTo(3);
    }

    /// <summary>An empty read past the gap closes a session. A file that could not be read is not an empty
    /// chat, and the session waits for the read that shows what is in it.</summary>
    [Test]
    public async Task A_chat_that_cannot_be_read_is_left_as_it_was_until_it_can() {
        var server  = new Server();
        var options = OneOldChat();
        var file    = Directory.GetFiles(Tmp.PathTo("chats")).Single();
        var chat    = await File.ReadAllTextAsync(file);
        var time    = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1790595501000).AddMinutes(1));
        var watcher = Watcher(server, time);

        await watcher.RunAsync(options, _log.Add, CancellationToken.None);
        await Assert.That(server.Posts.Select(p => p.Path)).IsEquivalentTo(["/hooks/session-start/grok-bot", "/hooks/transcript"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        time.Advance(TimeSpan.FromHours(3));
        await File.WriteAllTextAsync(file, chat[..(chat.Length / 2)]);
        await watcher.RunAsync(options, _log.Add, CancellationToken.None);

        await Assert.That(server.Posts.Count).IsEqualTo(2);
        await Assert.That(_log.Any(l => l.Contains("could not be read"))).IsTrue();

        await File.WriteAllTextAsync(file, chat);
        await watcher.RunAsync(options, _log.Add, CancellationToken.None);

        await Assert.That(server.Posts[^1].Path).IsEqualTo("/hooks/session-end/grok-bot");
        await Assert.That(server.Posts.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_source_that_is_not_there_is_no_source() {
        var server = new Server();

        await Assert.That(await RunAsync(server, new GrokBotWatchOptions(Desktop: Tmp.PathTo("absent"), Once: true))).IsEqualTo(GrokBotWatchOutcome.NoSource);
        await Assert.That(await RunAsync(server, new GrokBotWatchOptions(Gateway: Tmp.PathTo("gateway.json"), Once: true))).IsEqualTo(GrokBotWatchOutcome.NoSource);
        await Assert.That(server.Posts).IsEmpty();
    }

    [Test]
    public async Task A_second_watcher_is_refused_while_a_dry_run_is_not() {
        var server  = new Server();
        var options = OneOldChat();

        Tmp.CreateDir("config", "grok-bot");
        await using (new FileStream(Tmp.PathTo("config", "grok-bot", "watch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            await Assert.That(await RunAsync(server, options)).IsEqualTo(GrokBotWatchOutcome.AlreadyRunning);
            await Assert.That(await RunAsync(server, options with { DryRun = true })).IsEqualTo(GrokBotWatchOutcome.Completed);
        }

        await Assert.That(server.Posts).IsEmpty();
        await Assert.That(_log.Any(l => l.Contains("would open session"))).IsTrue();
    }
}
