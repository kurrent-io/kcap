using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Capture;
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

    GrokBotWatcher Watcher(Server server, TimeProvider? time = null, Func<string, int, RedactionOutcome>? redaction = null) => new(
        new ConfigRoot(Tmp.PathTo("config")), Resolutions.Of(new() { ServerUrl = "https://acme.kcap.ai/", DefaultVisibility = "org_public" }),
        new UserHome(Tmp.PathTo("home")), new FixedCapacitorHttpClient(background: server), time ?? TimeProvider.System) { Redaction = redaction };

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

    /// <summary>A chat is where a token gets pasted, and a private session is still stored: the entry goes
    /// out with the secret replaced and everything the server reads it by intact.</summary>
    [Test]
    public async Task A_secret_in_a_chat_is_replaced_before_it_is_sent() {
        var server  = new Server();
        var options = OneOldChat();
        var file    = Directory.GetFiles(Tmp.PathTo("chats")).Single();
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"hi\"", "\"use ghp_0123456789abcdefghijklmnopqrstuvwxyz\""));

        await Assert.That(await RunAsync(server, options)).IsEqualTo(GrokBotWatchOutcome.Completed);

        var body = server.Posts.Single(p => p.Path == "/hooks/transcript").Body;
        await Assert.That(body).DoesNotContain("ghp_");

        using var batch = JsonDocument.Parse(body);
        using var line  = JsonDocument.Parse(batch.RootElement.GetProperty("lines")[0].GetString()!);
        await Assert.That(line.RootElement.GetProperty("id").GetString()).IsEqualTo("t0u");
        await Assert.That(line.RootElement.GetProperty("content").GetString()).Contains("[REDACTED]");
    }

    /// <summary>An entry that ran out of time is neither sent raw nor passed over: its chat waits there,
    /// says so in the status, and goes on once a later try clears it.</summary>
    [Test]
    public async Task An_entry_that_cannot_be_cleared_in_time_holds_its_chat_until_it_can() {
        var server  = new Server();
        var time    = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1790595501000).AddMinutes(1));
        var watcher = Watcher(server, time, (line, tries) => new RedactionOutcome(line, tries == 0 ? RedactionLossReason.RegexTimeout : null, line.Length, line.Length));
        var options = OneOldChat() with { Once = false, Interval = TimeSpan.FromSeconds(10) };
        using var stop  = new CancellationTokenSource();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var running = watcher.RunAsync(options, _log.Add, stop.Token);

        while (watcher.Running() is not { Cycled: true, Unread: 1 }) await Task.Delay(10, limit.Token);

        while (watcher.Running() is not { Cycled: true, Unread: 0 }) {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(10, limit.Token);
        }

        await stop.CancelAsync();
        await Assert.That(await running).IsEqualTo(GrokBotWatchOutcome.Stopped);

        await Assert.That(server.Posts.Select(p => p.Path)).IsEquivalentTo(["/hooks/session-start/grok-bot", "/hooks/transcript"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(_log.Count(l => l.Contains("could not be cleared of secrets in time"))).IsEqualTo(1);
    }

    /// <summary>The desktop app keeps a long chat's newest entries only. A copy that has moved on past the
    /// last entry sent cannot give back what it dropped, so the watcher says so instead of sending the rest
    /// as if nothing were missing.</summary>
    [Test]
    public async Task A_copy_that_rolled_past_the_last_entry_sent_is_said_to_have() {
        var server  = new Server();
        var options = OneOldChat();
        var file    = Directory.GetFiles(Tmp.PathTo("chats")).Single();

        await RunAsync(server, options);

        await File.WriteAllTextAsync(file, "{\"schemaVersion\":1,\"value\":{\"entries\":["
            + """{"kind":"message","id":"t9u","role":"user","content":"later","timestampMs":1790695500000,"seq":40}"""
            + "]}}");
        await RunAsync(server, options);

        await Assert.That(_log.Count(l => l.Contains("now starts at seq 40, after seq 2"))).IsEqualTo(1);
        await Assert.That(server.Posts.Count(p => p.Path == "/hooks/transcript")).IsEqualTo(2);
    }

    /// <summary>A watcher keeps the server it started with, so its first line is the only way for whoever
    /// finds it running to learn where it records.</summary>
    [Test]
    public async Task The_first_log_line_names_the_server_it_posts_to() {
        await RunAsync(new Server(), OneOldChat(dryRun: true));

        await Assert.That(_log[0]).Contains("posting to https://acme.kcap.ai,");
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

    /// <summary>The lock proves a watcher runs, and only its holder writes the status, so a running watcher is
    /// described by what it wrote itself, down to the server having accepted what it sent. Once it stops, the
    /// file it left behind describes nothing.</summary>
    [Test]
    public async Task A_running_watcher_says_where_it_posts_and_a_stopped_one_says_nothing() {
        var watcher = Watcher(new Server());
        var options = OneOldChat() with { Once = false };
        using var stop = new CancellationTokenSource();

        await Assert.That(watcher.Running()).IsNull();

        var running = watcher.RunAsync(options, _ => { }, stop.Token);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        GrokBotWatchStatus? status;
        while ((status = watcher.Running())?.AcceptedAt is null) await Task.Delay(10, limit.Token);

        await Assert.That(status with { AcceptedAt = null }).IsEqualTo(new GrokBotWatchStatus("https://acme.kcap.ai", "default", Environment.ProcessId, Cycled: true));

        await stop.CancelAsync();
        await Assert.That(await running).IsEqualTo(GrokBotWatchOutcome.Stopped);
        await Assert.That(File.Exists(Tmp.PathTo("config", "grok-bot", "watch.json"))).IsTrue();
        await Assert.That(watcher.Running()).IsNull();
    }

    /// <summary>A watcher holds its lock while the server refuses everything it sends, so being found running
    /// says nothing about delivery: the status carries what the last cycle met.</summary>
    [Test]
    public async Task A_running_watcher_the_server_refuses_says_so() {
        var watcher = Watcher(new Server(HttpStatusCode.BadRequest));
        using var stop = new CancellationTokenSource();

        var running = watcher.RunAsync(OneOldChat() with { Once = false }, _ => { }, stop.Token);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        GrokBotWatchStatus? status;
        while ((status = watcher.Running())?.Failure is null) await Task.Delay(10, limit.Token);

        await Assert.That(status.AcceptedAt).IsNull();
        await Assert.That(GrokBotWatchStatus.Describe(status)).Contains("as profile default. Its last cycle failed: hooks/session-start/grok-bot answered 400");

        await stop.CancelAsync();
        await Assert.That(await running).IsEqualTo(GrokBotWatchOutcome.Stopped);
    }

    /// <summary>The install skill decides what to tell the user from these sentences.</summary>
    [Test]
    public async Task A_status_reads_as_what_the_last_cycle_met() {
        var found = new GrokBotWatchStatus("https://acme.kcap.ai", "default", 7);
        var at    = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        await Assert.That(GrokBotWatchStatus.Describe(null)).IsEqualTo("No Grok Bot watcher is running on this computer.");
        await Assert.That(GrokBotWatchStatus.Describe(new(null, null, null))).IsEqualTo("A Grok Bot watcher is running. Where it posts could not be read.");
        await Assert.That(GrokBotWatchStatus.Describe(found)).IsEqualTo("A Grok Bot watcher is running (pid 7), posting to https://acme.kcap.ai as profile default. Its first cycle has not finished.");
        await Assert.That(GrokBotWatchStatus.Describe(found with { Cycled = true })).EndsWith("Nothing has needed sending since it started.");
        await Assert.That(GrokBotWatchStatus.Describe(found with { Cycled = true, AcceptedAt = at })).EndsWith("The server last accepted a delivery at 2026-03-04 05:06:07Z.");
        await Assert.That(GrokBotWatchStatus.Describe(found with { Cycled = true, AcceptedAt = at, Failure = "refused" })).EndsWith("Its last cycle failed: refused.");
        await Assert.That(GrokBotWatchStatus.Describe(found with { Cycled = true, Unread = 1 })).EndsWith("default. Its last cycle could not read 1 chat, so nothing is sent for it yet.");
        await Assert.That(GrokBotWatchStatus.Describe(found with { Cycled = true, AcceptedAt = at, Unread = 2 })).EndsWith("05:06:07Z. Its last cycle could not read 2 chats, so nothing is sent for them yet.");
    }

    /// <summary>A chat that could not be read is skipped without failing the cycle, so a cycle that sent
    /// nothing because it read nothing must not pass for one that had nothing to send. Once the chat is
    /// gone it is no longer waiting to be read.</summary>
    [Test]
    public async Task A_running_watcher_says_how_many_chats_it_could_not_read() {
        var watcher = Watcher(new Server());
        var options = OneOldChat() with { Once = false, Interval = TimeSpan.FromMilliseconds(50) };
        var file    = Directory.GetFiles(Tmp.PathTo("chats")).Single();
        await File.WriteAllTextAsync(file, "{\"schemaVersion\":1,\"value\":{\"entr");
        using var stop = new CancellationTokenSource();

        var running = watcher.RunAsync(options, _ => { }, stop.Token);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        GrokBotWatchStatus? status;
        while ((status = watcher.Running()) is not { Cycled: true }) await Task.Delay(10, limit.Token);

        await Assert.That(status with { Pid = null }).IsEqualTo(new GrokBotWatchStatus("https://acme.kcap.ai", "default", null, Cycled: true, Unread: 1));

        // The watcher may have the file open for the moment a read takes.
        while (File.Exists(file)) {
            try {
                File.Delete(file);
            } catch (IOException) {
                await Task.Delay(10, limit.Token);
            }
        }

        while (watcher.Running() is not { Unread: 0, Cycled: true }) await Task.Delay(10, limit.Token);

        await stop.CancelAsync();
        await Assert.That(await running).IsEqualTo(GrokBotWatchOutcome.Stopped);
    }

    /// <summary>A status left by an earlier watcher sits under a lock a later one holds until that one writes
    /// its own. It names a process that is gone, and is not taken for the running watcher's.</summary>
    [Test]
    [Arguments("""{"pid":2147483646,"server":"https://stale.kcap.ai","profile":"default"}""")]
    [Arguments("{not json")]
    public async Task A_status_the_running_watcher_did_not_write_leaves_its_destination_unknown(string left) {
        Tmp.CreateFile(["config", "grok-bot", "watch.json"], left);

        await using (new FileStream(Tmp.CreateFile(["config", "grok-bot", "watch.lock"]), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.That(Watcher(new Server()).Running()).IsEqualTo(new GrokBotWatchStatus(null, null, null));
    }

    /// <summary>A session's id comes from where the gap falls. A run that sends under its own gap would
    /// store a chat's messages again under other sessions, so only a dry run may try one.</summary>
    [Test]
    public async Task A_gap_of_its_own_is_for_a_dry_run_only() {
        var server  = new Server();
        var options = OneOldChat() with { Gap = TimeSpan.FromMinutes(30) };

        await Assert.That(await RunAsync(server, options)).IsEqualTo(GrokBotWatchOutcome.GapNeedsDryRun);
        await Assert.That(await RunAsync(server, options with { DryRun = true })).IsEqualTo(GrokBotWatchOutcome.Completed);
        await Assert.That(server.Posts).IsEmpty();
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
