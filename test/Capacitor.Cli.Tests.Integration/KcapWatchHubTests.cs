using Capacitor.Cli.Core;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// The real <c>kcap watch</c> binary against a real SignalR hub, so what a person depends on is run
/// rather than inferred: lines arrive in order, a stop or a signal drains before the process leaves, and a
/// hub that is late or drops the connection costs nothing.
/// </summary>
[RunOn(OS.Linux | OS.MacOs)]
public class KcapWatchHubTests {
    [TempDir]         public required TempDir         Tmp     { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }

    static string User(string text) =>
        $$$"""{"type":"user","uuid":"{{{Guid.NewGuid():N}}}","message":{"role":"user","content":"{{{text}}}"}}""";

    static string Assistant(string text) =>
        $$$"""{"type":"assistant","uuid":"{{{Guid.NewGuid():N}}}","message":{"role":"assistant","content":[{"type":"text","text":"{{{text}}}"}]}}""";

    static string CursorLine(string text) =>
        $$$"""{"role":"user","message":{"content":[{"type":"text","text":"{{{text}}}"}]}}""";

    static string Numbers(IEnumerable<int> lineNumbers) => string.Join(",", lineNumbers);

    static void Append(string transcript, params string[] lines) =>
        File.AppendAllText(transcript, string.Concat(lines.Select(l => l + "\n")));

    string WatchersDir => Tmp.PathTo("watchers");

    static string NewSession() => Guid.NewGuid().ToString("N");

    Task<WatchedProcess> StartWatcherAsync(string url, string sessionId, string transcript, string vendor = "claude") =>
        WatchedProcess.StartAsync(Daemons, Config, Home, WatchersDir, url, sessionId, transcript, vendor);

    [Test]
    public async Task A_watcher_sends_a_transcript_and_drains_on_a_stop() {
        await using var hub        = await WatcherHubHost.StartAsync();
        var             sessionId  = NewSession();
        var             transcript = Tmp.CreateFile("t.jsonl", string.Concat(
            new[] { User("alpha"), Assistant("beta"), User("gamma") }.Select(l => l + "\n")));

        await using var watcher = await StartWatcherAsync(hub.Url, sessionId, transcript);

        await hub.WaitForAsync(h => h.ReceivedLineNumbers.Count >= 3, "the three lines");

        await Assert.That(Numbers(hub.ReceivedLineNumbers)).IsEqualTo("0,1,2");
        var sent = hub.Batches.SelectMany(b => b.Lines).ToArray();
        await Assert.That(sent[0]).Contains("alpha");
        await Assert.That(sent[1]).Contains("beta");
        await Assert.That(sent[2]).Contains("gamma");
        await Assert.That(hub.Connects.Single()).IsEqualTo((sessionId, (string?)null));
        await Assert.That(hub.Batches.All(b => b.SessionId == sessionId)).IsTrue();
        await Assert.That(hub.AckedBatches).IsEmpty();
        await Assert.That(hub.Drains).IsEmpty();

        await hub.PushStopAsync();
        await hub.WaitForAsync(h => h.Drains.Count == 1, "the drain-complete signal");

        await Assert.That(hub.Drains.Single()).IsEqualTo((sessionId, (string?)null));
        await Assert.That(await watcher.WaitForExitAsync("the watcher to exit")).IsEqualTo(0);
        await Assert.That(File.Exists(watcher.PidFile)).IsFalse();
    }

    [Test]
    public async Task A_line_appended_while_watching_is_sent() {
        await using var hub        = await WatcherHubHost.StartAsync();
        var             transcript = Tmp.CreateFile("t.jsonl", User("alpha") + "\n" + Assistant("beta") + "\n");

        await using var watcher = await StartWatcherAsync(hub.Url, NewSession(), transcript);

        await hub.WaitForAsync(h => h.ReceivedLineNumbers.Count >= 2, "the first two lines");
        Append(transcript, User("delta"));
        await hub.WaitForAsync(h => h.ReceivedLineNumbers.Count >= 3, "the appended line");

        await Assert.That(Numbers(hub.ReceivedLineNumbers)).IsEqualTo("0,1,2");
        await Assert.That(hub.Batches.SelectMany(b => b.Lines).Last()).Contains("delta");
    }

    [Test]
    public async Task A_terminate_signal_drains_before_exit() {
        await using var hub        = await WatcherHubHost.StartAsync();
        var             sessionId  = NewSession();
        var             transcript = Tmp.CreateFile("t.jsonl", User("alpha") + "\n" + Assistant("beta") + "\n");

        await using var watcher = await StartWatcherAsync(hub.Url, sessionId, transcript);

        await hub.WaitForAsync(h => h.ReceivedLineNumbers.Count >= 2, "the first two lines");
        Append(transcript, User("delta"), Assistant("epsilon"));
        watcher.SignalTerm();

        await Assert.That(await watcher.WaitForExitAsync("the watcher to exit")).IsEqualTo(0);

        await Assert.That(Numbers(hub.ReceivedLineNumbers)).IsEqualTo("0,1,2,3");
        await Assert.That(hub.Drains.Single()).IsEqualTo((sessionId, (string?)null));
        await Assert.That(File.Exists(watcher.PidFile)).IsFalse();
    }

    [Test]
    public async Task A_watcher_connects_once_the_hub_comes_up() {
        await using var hub = await WatcherHubHost.StartAsync(down: true);
        var transcript = Tmp.CreateFile("t.jsonl", User("alpha") + "\n" + Assistant("beta") + "\n");

        await using var watcher = await StartWatcherAsync(hub.Url, NewSession(), transcript);

        await watcher.WaitForLogAsync("SignalR connect failed", "the first failed connect");

        hub.ComeUp();
        await hub.WaitForAsync(h => h.ReceivedLineNumbers.Count >= 2, "the lines, once the hub is up");

        await Assert.That(Numbers(hub.ReceivedLineNumbers)).IsEqualTo("0,1");

        await hub.PushStopAsync();
        await Assert.That(await watcher.WaitForExitAsync("the watcher to exit")).IsEqualTo(0);
    }

    [Test]
    public async Task A_stop_before_connect_exits_early() {
        await using var hub = await WatcherHubHost.StartAsync(down: true);
        var transcript = Tmp.CreateFile("t.jsonl", User("alpha") + "\n" + Assistant("beta") + "\n");

        await using var watcher = await StartWatcherAsync(hub.Url, NewSession(), transcript);

        await watcher.WaitForLogAsync("SignalR connect failed", "the first failed connect");
        watcher.SignalTerm();

        await Assert.That(await watcher.WaitForExitAsync("the watcher to exit")).IsEqualTo(0);

        // Nothing was registered with any server, so the unsent lines are neither spooled nor flagged for
        // import: they stay in the transcript alone.
        var spool = Config.PathTo(TranscriptSpool.DirectoryName);
        await Assert.That(Directory.Exists(spool) && Directory.EnumerateFiles(spool, "*", SearchOption.AllDirectories).Any()).IsFalse();
    }

    /// <summary>The connect has left the client, so this is not a stop before connect: nobody can say nothing was
    /// registered, and the watcher fails as it always has, without waiting for the server's answer.</summary>
    [Test]
    public async Task A_stop_while_the_server_holds_the_connect_fails_the_watcher() {
        await using var hub        = await WatcherHubHost.StartAsync();
        var             landed     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             transcript = Tmp.CreateFile("t.jsonl", User("alpha") + "\n" + Assistant("beta") + "\n");
        hub.ConnectHold = _ => landed.Task;

        await using var watcher = await StartWatcherAsync(hub.Url, NewSession(), transcript);

        await hub.WaitForAsync(h => h.Connects.Count == 1, "the connect to reach the server");
        watcher.SignalTerm();

        try {
            await Assert.That(await watcher.WaitForExitAsync("the watcher to exit")).IsEqualTo(1);
            await Assert.That(hub.Drains).IsEmpty();
        } finally {
            landed.TrySetResult();
        }
    }

    [Test]
    public async Task A_dropped_connection_re_registers_and_carries_on() {
        await using var hub        = await WatcherHubHost.StartAsync();
        var             transcript = Tmp.CreateFile("t.jsonl", string.Concat(
            new[] { User("alpha"), Assistant("beta"), User("gamma") }.Select(l => l + "\n")));

        // The first connect starts at the beginning; the second is told the server holds only line 0.
        hub.ResumeFrom = connects => connects == 1 ? 0 : 1;

        await using var link    = SeverableProxy.Start(hub.Url);
        await using var watcher = await StartWatcherAsync(link.Url, NewSession(), transcript);

        await hub.WaitForAsync(h => h.ReceivedLineNumbers.Count >= 3, "the three lines");
        // A separate batch proves the first send completed on the recorder; server receipt
        // alone can precede its SignalR completion and leave the local frontier at zero.
        Append(transcript, Assistant("pre-drop checkpoint"));
        await hub.WaitForAsync(h => h.ReceivedLineNumbers.Contains(3), "the checkpoint after the first send completed");
        link.Sever();
        await hub.WaitForAsync(h => h.Connects.Count == 2, "the watcher to register again");
        await watcher.WaitForLogAsync("Server behind (1 vs ", "the recorder to rewind to the server's position");
        await hub.WaitForAsync(h => h.ReceivedLineNumbers.Count >= 7, "the lines after the server's position");
        await Assert.That(Numbers(hub.ReceivedLineNumbers)).IsEqualTo("0,1,2,3,1,2,3");

        Append(transcript, Assistant("delta"));
        await hub.WaitForAsync(h => h.ReceivedLineNumbers.Contains(4), "the line appended after the reconnect");

        await Assert.That(Numbers(hub.ReceivedLineNumbers)).IsEqualTo("0,1,2,3,1,2,3,4");
    }

    [Test]
    public async Task A_cursor_watcher_uses_the_acked_send() {
        await using var hub        = await WatcherHubHost.StartAsync();
        var             transcript = Tmp.CreateFile("t.jsonl", CursorLine("alpha") + "\n" + CursorLine("beta") + "\n");

        await using var watcher = await StartWatcherAsync(hub.Url, NewSession(), transcript, vendor: "cursor");

        await hub.WaitForAsync(h => h.ReceivedLineNumbers.Count >= 2, "the two lines");

        await Assert.That(Numbers(hub.AckedBatches.SelectMany(b => b.LineNumbers!))).IsEqualTo("0,1");
        await Assert.That(hub.Batches).IsEmpty();
    }
}
