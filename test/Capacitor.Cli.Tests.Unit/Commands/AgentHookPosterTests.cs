using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Tests for the shared agent-hook recording POST. This is the seam the non-Claude
/// hooks (Codex, Gemini, Copilot, Pi, Kiro, OpenCode) delegate to. Its job is to SKIP a POST
/// that would 401 because auth has lapsed — reporting <see cref="HookPostOutcome.AuthLapsed"/>
/// without touching stderr or the server — while leaving the authenticated success path and
/// the real-failure path (stderr + <see cref="HookPostOutcome.Failed"/>) unchanged.
///
/// The (client, status) factory is injected so the auth outcome is controlled directly and no
/// token store or /auth/config discovery is needed; the POST itself goes to a WireMock server.
/// </summary>
public class AgentHookPosterTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // The poster targets the resolution's URL, so the stub server's is what the resolution names.
    AgentHookPoster  Poster => field ??= new(Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient()), TimeProvider.System);

    public void Dispose() => _server.Stop();

    static Func<Task<AuthAttempt>> Factory(AuthStatus status)
        => () => Task.FromResult(new AuthAttempt(new HttpClient(), status));

    [Test]
    public async Task Expired_auth_skips_the_post_and_reports_AuthLapsed() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var outcome = await Poster.PostAsync(
            Factory(AuthStatus.Expired), "session-start/codex", "{}", "codex-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.AuthLapsed);

        // The doomed POST must never be sent.
        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NotAuthenticated_skips_the_post_and_reports_AuthLapsed() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/gemini").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var outcome = await Poster.PostAsync(
            Factory(AuthStatus.NotAuthenticated), "session-start/gemini", "{}", "gemini-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.AuthLapsed);

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/gemini").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Ok_auth_posts_the_body_and_reports_Posted() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var outcome = await Poster.PostAsync(
            Factory(AuthStatus.Ok), "session-start/pi", """{"hello":"world"}""", "pi-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Posted);

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/pi").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0].RequestMessage.Body).IsEqualTo("""{"hello":"world"}""");
    }

    [Test]
    public async Task NoAuthRequired_posts_normally() {
        // "None" provider → the client is usable as-is; behave exactly like authenticated.
        _server.Given(Request.Create().WithPath("/hooks/session-start/opencode").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var outcome = await Poster.PostAsync(
            Factory(AuthStatus.NoAuthRequired), "session-start/opencode", "{}", "opencode-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Posted);

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/opencode").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Server_error_reports_Failed() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        var outcome = await Poster.PostAsync(
            Factory(AuthStatus.Ok), "session-start/kiro", "{}", "kiro-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Failed);
    }

    /// <summary>
    /// <c>PostAsync</c> has no spool, so a 401 stays <see cref="HookPostOutcome.Failed"/> here, while
    /// the line the user actually reads names the fix. These vendors have no user-facing stdout
    /// channel (their stdout is a handshake contract the vendor parses), so stderr is the only place
    /// a nudge can go.
    /// </summary>
    // Globally sequential rather than keyed. "ConsoleErrorRedirect" has no other member, so it
    // serialized this Console.Error capture against nothing: Server_error_reports_Failed below
    // writes to Console.Error with no serialization at all and could land inside this window, and
    // the classic save/restore interleave with this suite's other Console.SetError sites could
    // leave Console.Error pointing at an abandoned StringWriter. Bare NotInParallel serializes
    // against every other bare-NotInParallel test in the assembly, which is what this needs.
    [Test, NotInParallel]
    public async Task Unauthorized_reports_Failed_and_names_kcap_login_on_stderr() {
        _server.Given(Request.Create().WithPath("/hooks/stop/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var capture = ConsoleOutput.StartErrorCapture("\n");
        HookPostOutcome outcome;

        outcome = await Poster.PostAsync(
            Factory(AuthStatus.Ok), "stop/codex", "{}", "codex-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Failed);
        await Assert.That(capture.GetCapturedError().Trim()).IsEqualTo(
            AuthRejectionNotice.VendorStderrLine("codex-hook", "stop/codex", 401));
    }

    [Test]
    public async Task IsAuthLapsed_is_true_for_every_unusable_status() {
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.Expired)).IsTrue();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.NotAuthenticated)).IsTrue();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.WrongServer)).IsTrue();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.UnusableServerUrl)).IsTrue();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.Ok)).IsFalse();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.NoAuthRequired)).IsFalse();
    }

    [Test]
    public async Task PostOrSpool_on_auth_lapse_spools_and_returns_Spooled() {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        var outcome = await Poster.PostOrSpoolAsync(
            () => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Expired)), "session-start/kiro", """{"session_id":"x"}""",
            "kiro-hook", spool, sessionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", route: "session-start/kiro");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Spooled);
        await Assert.That(spool.HasBacklog("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).IsTrue();
    }

    /// <summary>A start posted live for a session whose spooled end was delivered leaves a note in the spool, so
    /// the run's later spooled hooks are not taken for the ended run's stragglers.</summary>
    [Test]
    public async Task PostOrSpool_notes_a_start_posted_for_an_ended_session() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        const string sid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        spool.MarkEnded(sid);

        var outcome = await Poster.PostOrSpoolAsync(Factory(AuthStatus.Ok), "session-start/kiro", """{"session_id":"x"}""",
            "kiro-hook", spool, sessionId: sid, route: "session-start/kiro");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Posted);
        await Assert.That(spool.HasBacklog(sid)).IsTrue();
    }

    /// <summary>The no-spool path is bounded the same way: past the cap it reports the lapse outcome
    /// its callers already exit cleanly on, instead of holding the hook until its host kills it.</summary>
    [Test]
    public async Task Post_reports_AuthLapsed_when_auth_outlives_its_cap() {
        _server.Given(Request.Create().WithPath("/hooks/session-end/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        var never     = new TaskCompletionSource<AuthAttempt>();
        var sw        = System.Diagnostics.Stopwatch.StartNew();
        var handedOff = false;

        var outcome = await Poster.PostAsync(
            () => never.Task, "session-end/pi", "{}", "pi-hook",
            authCap: TimeSpan.FromMilliseconds(100), onAuthAbandoned: () => handedOff = true);

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.AuthLapsed);
        await Assert.That(handedOff).IsTrue();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        await Assert.That(_server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/pi").UsingPost()).Count).IsEqualTo(0);
    }

    /// <summary>Client creation can wait on the cross-process refresh lock for longer than any host
    /// lets a hook live. Past the cap the payload is spooled and the caller proceeds, rather than the
    /// hook being killed on its way to that same spool.</summary>
    [Test]
    public async Task PostOrSpool_spools_when_auth_outlives_its_cap() {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        var never     = new TaskCompletionSource<AuthAttempt>();
        var sw        = System.Diagnostics.Stopwatch.StartNew();
        var handedOff = false;

        var outcome = await Poster.PostOrSpoolAsync(
            () => never.Task, "session-start/kiro", """{"session_id":"x"}""",
            "kiro-hook", spool, sessionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", route: "session-start/kiro",
            authCap: TimeSpan.FromMilliseconds(100), onAuthAbandoned: () => handedOff = true);

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Spooled);
        await Assert.That(handedOff).IsTrue();
        await Assert.That(spool.HasBacklog("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).IsTrue();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>A server-rejected credential is repaired by <c>kcap login</c>, so the payload is kept
    /// for the drain that follows the login instead of being lost with the turn that hit the 401.</summary>
    [Test]
    public async Task PostOrSpool_on_401_spools_and_returns_Spooled() {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        using var handler = new StubHandler(System.Net.HttpStatusCode.Unauthorized);
        var outcome = await Poster.PostOrSpoolAsync(
            () => Task.FromResult(new AuthAttempt(new HttpClient(handler), AuthStatus.Ok)), "session-start/kiro", """{"session_id":"x"}""",
            "kiro-hook", spool, sessionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", route: "session-start/kiro");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Spooled);
        await Assert.That(spool.HasBacklog("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).IsTrue();
    }

    /// <summary>Non-vacuous control: a 4xx that rejects the payload itself still fails outright, so
    /// the test above proves 401 is singled out rather than that every 4xx now spools.</summary>
    [Test]
    public async Task PostOrSpool_on_a_payload_rejecting_4xx_returns_Failed_and_spools_nothing() {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        using var handler = new StubHandler(System.Net.HttpStatusCode.BadRequest);
        var outcome = await Poster.PostOrSpoolAsync(
            () => Task.FromResult(new AuthAttempt(new HttpClient(handler), AuthStatus.Ok)), "session-start/kiro", """{"session_id":"x"}""",
            "kiro-hook", spool, sessionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", route: "session-start/kiro");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Failed);
        await Assert.That(spool.HasBacklog("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).IsFalse();
    }

    /// <summary>Spooling a 401 must not silence the nudge: stderr is still the only place these
    /// vendors can name <c>kcap login</c>. Redirects the process-global Console.Error, so it runs alone.</summary>
    [Test, NotInParallel]
    public async Task PostOrSpool_on_401_still_names_kcap_login_on_stderr() {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        using var handler = new StubHandler(System.Net.HttpStatusCode.Unauthorized);
        using var capture = ConsoleOutput.StartErrorCapture("\n");

        await Poster.PostOrSpoolAsync(
            () => Task.FromResult(new AuthAttempt(new HttpClient(handler), AuthStatus.Ok)), "stop/codex", """{"session_id":"x"}""",
            "codex-hook", spool, sessionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", route: "stop/codex");

        await Assert.That(capture.GetCapturedError()).Contains(
            AuthRejectionNotice.VendorStderrLine("codex-hook", "stop/codex", 401));
    }

    [Test]
    public async Task PostOrSpool_on_success_returns_Posted_and_spools_nothing() {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        using var handler = new StubHandler(System.Net.HttpStatusCode.OK); // 200
        var outcome = await Poster.PostOrSpoolAsync(
            () => Task.FromResult(new AuthAttempt(new HttpClient(handler), AuthStatus.Ok, null, null)), "session-start/kiro", """{"session_id":"x"}""",
            "kiro-hook", spool, sessionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", route: "session-start/kiro");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Posted);
        await Assert.That(spool.HasBacklog("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).IsFalse();
    }

    /// <summary>Nothing spooled means nothing to post, so the hook spends nothing on a credential.</summary>
    [Test]
    public async Task A_drain_with_nothing_spooled_never_builds_a_client() {
        var entered = false;

        await Poster.DrainSpoolsCoreAsync(
            new HookSpool(Config.Root, TimeProvider.System, server: null), new TranscriptSpool(Config.Root, TimeProvider.System, server: null), null,
            _ => {
                entered = true;
                throw new InvalidOperationException("a client was built for an empty spool");
            });

        await Assert.That(entered).IsFalse();
    }

    /// <summary>Every hook's drain moves a handed Muse end no watcher took into the spool, so the session still ends
    /// when the daemon is not running and no hook for that session runs again.</summary>
    [Test]
    public async Task A_drain_spools_an_unattended_handed_Muse_end() {
        const string session = "01a13694b0ad7ae28bf52af6357a0847";
        const string payload = """{"hook_event_name":"SessionEnd","reason":"exit"}""";
        var paths     = WatcherPaths.FromEnvironment(Config.Root);
        var lifecycle = new HookSpool(Config.Root, TimeProvider.System, server: null);
        Capacitor.Cli.Core.Harness.Muse.MuseSessionEndRequest.Write(paths, session, payload);
        File.SetLastWriteTimeUtc(Capacitor.Cli.Core.Harness.Muse.MuseSessionEndRequest.PathFor(paths, session, payload), DateTime.UtcNow.AddMinutes(-10));

        await Poster.DrainSpoolsCoreAsync(lifecycle, new TranscriptSpool(Config.Root, TimeProvider.System, server: null), null,
            _ => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Expired)));

        await Assert.That(lifecycle.HasBacklog(session)).IsTrue();
    }

    /// <summary>A drain still in flight when its hook process is killed must not keep the next one from running:
    /// the throttle counts only a drain that returned.</summary>
    [Test]
    public async Task A_lifecycle_drain_still_in_flight_does_not_suppress_the_next() {
        var lifecycle  = new HookSpool(Config.Root, TimeProvider.System, server: null);
        var transcript = new TranscriptSpool(Config.Root, TimeProvider.System, server: null);
        lifecycle.Append("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "session-start/codex", "{}");
        var hold = new TaskCompletionSource<AuthAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = false;
        var entered  = false;

        var first = Poster.DrainSpoolsCoreAsync(lifecycle, transcript, null, _ => {
            inFlight = true;
            return hold.Task;
        });
        await Assert.That(inFlight).IsTrue();

        await Poster.DrainSpoolsCoreAsync(lifecycle, transcript, null, _ => {
            entered = true;
            return Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Expired));
        });

        await Assert.That(entered).IsTrue();
        hold.SetResult(new AuthAttempt(new HttpClient(), AuthStatus.Expired));
        await first;
    }

    /// <summary>The counterpart that keeps the test above honest: a spooled entry still gets a client.</summary>
    [Test]
    public async Task A_drain_with_a_spooled_entry_builds_a_client() {
        var lifecycle = new HookSpool(Config.Root, TimeProvider.System, server: null);
        lifecycle.Append("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "session-start/codex", "{}");
        var entered = false;

        await Poster.DrainSpoolsCoreAsync(
            lifecycle, new TranscriptSpool(Config.Root, TimeProvider.System, server: null), null,
            _ => {
                entered = true;
                return Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Expired));
            });

        await Assert.That(entered).IsTrue();
    }
}

sealed class StubHandler(System.Net.HttpStatusCode code) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(code));
}
