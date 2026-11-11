using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// A successful terminal spool delivery carrying generate_whats_done must invoke the generator
/// callback for every vendor. Exercise the production HttpClient overload against WireMock;
/// nonterminal routes, failed delivery and responses without that signal must not invoke it.
/// </summary>
public class LifecycleSpoolDrainWhatsDoneTests : IDisposable {
    CursorMarkers Markers => new(Config.Root, TimeProvider.System);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    const string Sid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public void Dispose() => _server.Stop();

    [Test]
    public async Task fires_for_a_non_claude_vendor_session_end_with_generate_whats_done() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":true}"""));

        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-end/kiro", $$"""{"session_id":"{{Sid}}"}""");

        var calls = new List<(string SessionId, string Vendor)>();
        using var client = new HttpClient();
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LifecycleSpoolDrain.RunAsync(Config.Root, Markers, client, _server.Url!, life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: cts.Token,
            onWhatsDoneRequested: (sid, vendor) => calls.Add((sid, vendor)), time: TimeProvider.System);

        await Assert.That(calls).IsEquivalentTo([(Sid, "kiro")]);
    }

    [Test]
    public async Task fires_for_claudes_literal_session_end_route_defaulting_to_claude_vendor() {
        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":true}"""));

        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");

        var calls = new List<(string SessionId, string Vendor)>();
        using var client = new HttpClient();
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LifecycleSpoolDrain.RunAsync(Config.Root, Markers, client, _server.Url!, life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: cts.Token,
            onWhatsDoneRequested: (sid, vendor) => calls.Add((sid, vendor)), time: TimeProvider.System);

        await Assert.That(calls).IsEquivalentTo([(Sid, "claude")]);
    }

    [Test]
    public async Task does_not_fire_when_generate_whats_done_is_absent() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-end/kiro", $$"""{"session_id":"{{Sid}}"}""");

        var fired = false;
        using var client = new HttpClient();
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LifecycleSpoolDrain.RunAsync(Config.Root, Markers, client, _server.Url!, life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: cts.Token,
            onWhatsDoneRequested: (_, _) => fired = true, time: TimeProvider.System);

        await Assert.That(fired).IsFalse();
    }

    [Test]
    public async Task does_not_fire_for_a_non_terminal_route_even_with_generate_whats_done_true() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":true}"""));

        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-start/kiro", $$"""{"session_id":"{{Sid}}"}""");

        var fired = false;
        using var client = new HttpClient();
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await LifecycleSpoolDrain.RunAsync(Config.Root, Markers, client, _server.Url!, life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: cts.Token,
            onWhatsDoneRequested: (_, _) => fired = true, time: TimeProvider.System);

        await Assert.That(fired).IsFalse();
    }

    [Test]
    public async Task omitting_the_callback_never_throws() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":true}"""));

        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-end/kiro", $$"""{"session_id":"{{Sid}}"}""");

        using var client = new HttpClient();
        using var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // No onWhatsDoneRequested — the daemon's periodic drain and callers that don't need
        // the side effect may omit it; this must never throw.
        await LifecycleSpoolDrain.RunAsync(Config.Root, Markers, client, _server.Url!, life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: cts.Token, time: TimeProvider.System);

        await Assert.That(life.HasBacklog(Sid)).IsFalse();
    }
}
