using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Tests for the periodic spool-drain tick's auth handling.</summary>
public class SpoolDrainLoopTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SpoolDrainLoop MakeLoop(ICapacitorHttpClient http, ILogger logger) =>
        new(Config.Root, http, "https://example.invalid",
            new HookSpool(Config.PathTo("lifecycle"), time: TimeProvider.System), new TranscriptSpool(Config.PathTo("transcript"), time: TimeProvider.System), logger, TimeProvider.System);

    [Test]
    public async Task Tick_treats_UnusableServerUrl_as_a_lapse_and_skips_the_pass() {
        var http   = new StatusOnlyHttpClient(AuthStatus.UnusableServerUrl);
        var logger = new CapturingLogger<SpoolDrainLoopTests>();

        await MakeLoop(http, logger).TickAsync(CancellationToken.None);

        await Assert.That(logger.Entries.Any(e => e.Message.Contains("auth lapsed — skipping this pass"))).IsTrue();
    }

    /// <summary>The tick moves a handed Muse end no watcher took into the lifecycle spool, auth or not, so the session
    /// still ends when no hook for it runs again.</summary>
    [Test]
    public async Task Tick_spools_an_unattended_handed_Muse_end() {
        const string session = "01a13694b0ad7ae28bf52af6357a0847";
        const string payload = """{"hook_event_name":"SessionEnd","reason":"exit"}""";
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        Capacitor.Cli.Core.Harness.Muse.MuseSessionEndRequest.Write(paths, session, payload);
        File.SetLastWriteTimeUtc(Capacitor.Cli.Core.Harness.Muse.MuseSessionEndRequest.PathFor(paths, session, payload), DateTime.UtcNow.AddMinutes(-10));

        await MakeLoop(new StatusOnlyHttpClient(AuthStatus.UnusableServerUrl), new CapturingLogger<SpoolDrainLoopTests>()).TickAsync(CancellationToken.None);

        await Assert.That(new HookSpool(Config.PathTo("lifecycle"), time: TimeProvider.System).HasBacklog(session)).IsTrue();
        await Assert.That(Capacitor.Cli.Core.Harness.Muse.MuseSessionEndRequest.Exists(paths, session)).IsFalse();
    }

    /// <summary>The tick pays a span close a hook's drain owed, auth or not: the close only spools its changes.</summary>
    [Test]
    public async Task Tick_closes_an_owed_Copilot_span() {
        const string session = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        var end = $$"""{"session_id":"{{session}}","reason":"user_exit"}""";
        Capacitor.Cli.Recording.Harness.Copilot.CopilotSpanClose.Record(paths, session, "https://example.invalid", end);
        Capacitor.Cli.Recording.Harness.Copilot.CopilotSpanClose.NoteOutcome(paths, "session-end/copilot", end, DrainOutcome.Delivered);

        await MakeLoop(new StatusOnlyHttpClient(AuthStatus.UnusableServerUrl), new CapturingLogger<SpoolDrainLoopTests>()).TickAsync(CancellationToken.None);

        await Assert.That(Capacitor.Cli.Recording.Harness.Copilot.CopilotSpanClose.Pending(paths, "https://example.invalid")).DoesNotContain(session);
    }

    // Credential resolution must run on the loop's own token, not one truncated to the drain
    // budget — a proactive refresh routinely outlives that budget.
    [Test]
    public async Task Tick_resolves_the_credential_on_the_loops_own_token() {
        using var loopCts = new CancellationTokenSource();
        var       http    = new StatusOnlyHttpClient(AuthStatus.Ok);
        var       logger  = new CapturingLogger<SpoolDrainLoopTests>();

        await MakeLoop(http, logger).TickAsync(loopCts.Token);

        await Assert.That(http.ReceivedToken).IsEqualTo(loopCts.Token);
    }

    sealed class StatusOnlyHttpClient(AuthStatus status) : ICapacitorHttpClient {
        public CancellationToken ReceivedToken { get; private set; }

        public Task<HttpClient> ForCommandAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient());

        public Task<HttpClient> ForSessionAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient());

        public Task<AuthAttempt> ForHookAsync(CancellationToken ct = default) {
            ReceivedToken = ct;
            return Task.FromResult(new AuthAttempt(new HttpClient(), status, null, null));
        }

        public Task<AuthAttempt> ForWaitAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthAttempt(new HttpClient(), status, null, null));

        public Task<AuthAttempt> ForProtectedReadAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthAttempt(new HttpClient(), status, null, null));

        public Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient());

        public Task<HttpClient> ForMemoryAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient());

        public HttpClient Anonymous() => new();

        public HttpClient Loopback() => new();

        public HttpClient Bearer() => new();
    }
}
