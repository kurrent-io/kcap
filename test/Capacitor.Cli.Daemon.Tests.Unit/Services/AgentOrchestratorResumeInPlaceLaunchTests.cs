using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// An in-place resume relaunches the ended session natively in its own directory, bound to the original session, and
/// holds the source's lease from arrival until the readiness window has passed. A launch that cannot work is refused
/// before anything starts; one that fails after its agent published is retired before it is reported, once, and
/// nothing in the directory is ever removed.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorResumeInPlaceLaunchTests {
    const string AgentId = "resume-1";
    const string Session = "0123456789abcdef0123456789abcdef";
    const string Dashed  = "01234567-89ab-cdef-0123-456789abcdef";
    const string Token   = "lease-token";

    [TempDir] public required TempDir Tmp { get; init; }

    sealed class LoadableFactory(string vendor) : IHostedAgentRuntimeFactory {
        public string CliPath            => "unused";
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended => false;
        public bool   ResumesIdle        { get; init; }
        public bool   Reopens            { get; init; }
        public int?   ExitWith           { get; init; }
        public bool   ExitsLater         { get; init; }
        public TimeSpan StartDelay       { get; init; }
        public bool     Unkillable       { get; init; }

        public RuntimeStartContext? Context    { get; private set; }
        public FakeAcpRuntime?      AcpRuntime { get; private set; }
        public FakeHostedAgentRuntime? PtyRuntime { get; private set; }
        public int                  Starts     { get; private set; }

        public bool IsAvailable() => true;
        public bool SupportsRestartResume(string? runtimeTransport) => true;
        public bool ResumesIdleWithoutPrompt(string? runtimeTransport) => ResumesIdle;

        public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            Starts++;
            Context = ctx;
            if (StartDelay > TimeSpan.Zero) await Task.Delay(StartDelay, ct);

            if (Reopens) {
                AcpRuntime = new FakeAcpRuntime { Vendor = Vendor, Cwd = ctx.Worktree.Path, AcpSessionId = ctx.ResumeSessionId!, Reopened = true };

                return new HostedRuntimeStart(AcpRuntime, McpConfigPath: null, Transcript: AcpRuntime);
            }

            PtyRuntime = new FakeHostedAgentRuntime(Vendor, emitsTerminalOutput: true) {
                ExitCode = ExitWith ?? 0, IgnoresTerminate = Unkillable,
                // A pid no real process can have, so cleanup's own liveness check never reaches one.
                Pid = Unkillable ? int.MaxValue : 0, StartIdentity = Unkillable ? "test-identity" : null,
            };
            if (ExitWith is not null) PtyRuntime.ExitGate.SetResult();
            if (ExitsLater) _ = Task.Delay(100, CancellationToken.None).ContinueWith(_ => PtyRuntime.ExitGate.TrySetResult(), TaskScheduler.Default);

            return new HostedRuntimeStart(PtyRuntime, McpConfigPath: null, Transcript: null);
        }
    }

    sealed class Fixture : IAsyncDisposable {
        public Fixture(TempDir tmp, LoadableFactory factory, CaptureServerConnection? server = null, LaunchConsentGate? consent = null,
                TimeSpan? readiness = null) {
            Directory.CreateDirectory(tmp.PathTo("checkout"));
            Checkout = tmp.GetResolvedPath("checkout");
            File.WriteAllText(Path.Combine(Checkout, "work.txt"), "uncommitted");
            Store    = AccountDirectory.Normalize(tmp.CreateDir(".claude"));
            Factory  = factory;
            Server   = server ?? new CaptureServerConnection();
            Orch     = AgentOrchestratorHarness.BuildOrchestrator(
                Server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: Checkout, extraRuntimeFactories: [factory], consentGate: consent,
                configure: c => {
                    c.ResumeReadinessWindow = readiness ?? TimeSpan.FromMilliseconds(300);
                    c.Accounts              = new AccountStore(tmp.PathTo("accounts"));
                    c.DefaultDirectories    = new Dictionary<HarnessId, string> { [HarnessId.Claude] = Store };
                });
            Orch.SourceLeaseRenewal = TimeSpan.FromMilliseconds(50);
        }

        public string                  Checkout { get; }
        public string                  Store    { get; }
        public LoadableFactory         Factory  { get; }
        public CaptureServerConnection Server   { get; }
        public AgentOrchestrator       Orch     { get; }

        public LaunchAgentCommand Launch(string? prompt = "carry on", string session = Session, string? store = null, string? directory = null) =>
            new(AgentId: AgentId, Prompt: prompt, Model: "", Effort: null, RepoPath: directory ?? Checkout, Tools: null, AttachmentIds: null,
                Vendor: Factory.Vendor) {
                ResumeInPlace = new ResumeInPlaceLaunch(session, directory ?? Checkout, Token, null, store ?? Store),
            };

        public string[] Failures() => [.. Server.LaunchFailedCalls.Where(c => c.AgentId == AgentId).Select(c => c.Reason)];

        public async ValueTask DisposeAsync() => await Orch.DisposeAsync();
    }

    [Test]
    public async Task A_resume_runs_in_its_directory_on_the_original_session() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsEmpty();
        var ctx = f.Factory.Context!;
        await Assert.That((ctx.ResumeSessionId, ctx.Worktree.Path, ctx.Work, ctx.Prompt)).IsEqualTo((Dashed, f.Checkout, WorkLocation.InPlace, "carry on"));
        await Assert.That(ctx.Account?.Directory).IsEqualTo(f.Store);
        var agent = f.Orch.GetAgentForTest(AgentId)!;
        await Assert.That(agent.SessionId).IsEqualTo(Session);
        await Assert.That(agent.ResumePending).IsFalse();
        var ledgered = f.Orch.ResumeLedger!.TryRead(AgentId)!;
        await Assert.That((ledgered.Launch.Work, ledgered.State, ledgered.VendorSession)).IsEqualTo((WorkLocation.InPlace, ResumeState.Running, Dashed));
        await Assert.That(agent.Checkout.BorrowedFrom).IsEqualTo(f.Checkout);
    }

    [Test]
    public async Task An_empty_first_message_resumes_idle() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude") { ResumesIdle = true });

        await f.Orch.HandleLaunchAgentForTest(f.Launch(prompt: ""));

        await Assert.That(f.Failures()).IsEmpty();
        await Assert.That(f.Factory.Context!.Prompt).IsEqualTo("");
        await Assert.That(f.Orch.GetAgentForTest(AgentId)!.ActivityClock.AwaitingInput).IsTrue();
    }

    [Test]
    public async Task The_lease_is_renewed_until_readiness_and_ended_after() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"), readiness: TimeSpan.FromMilliseconds(400));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Server.HandoffRenews).Contains(Token);
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task A_denied_consent_starts_nothing_and_ends_the_lease() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"), consent: AgentOrchestratorHarness.DenyDefaultGate(Tmp.CreateDir("consent")));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures().Single()).StartsWith(LaunchConsentGate.DeniedReasonPrefix);
        await Assert.That(f.Factory.Starts).IsEqualTo(0);
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task A_refused_acp_bind_retires_the_runtime_and_reports_it() {
        var server = new CaptureServerConnection { AcpEventsAckOverride = _ => new AcpBatchAck(-1, -1, Rejected: true) };
        await using var f = new Fixture(Tmp, new LoadableFactory("cursor") { Reopens = true }, server);

        await f.Orch.HandleLaunchAgentForTest(f.Launch(session: "sess-1", store: AccountDirectory.Normalize(
            Directory.CreateDirectory(f.Orch.HarnessesForTest.Of<Capacitor.Cli.Core.Harness.Cursor.CursorHarness>().Paths.CursorDir).FullName)));

        await Assert.That(f.Failures()).IsEquivalentTo([$"{ResumeInPlaceRefusals.Failed}: acp_binding_unavailable"]);
        await Assert.That(f.Factory.AcpRuntime!.HasExited).IsTrue();
        await Assert.That(f.Factory.AcpRuntime!.ReleaseFirstTurnCalls).IsEqualTo(0);
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest(AgentId)?.Status is null or "Completed" or "Failed");
        await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)).IsNull();
        await Assert.That(File.Exists(Path.Combine(f.Checkout, "work.txt"))).IsTrue();
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task A_runtime_that_exits_at_once_is_reported_and_deletes_nothing() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude") { ExitWith = 3 });

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsNotEmpty();
        await Assert.That(f.Factory.Starts).IsEqualTo(1);
        await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)).IsNull();
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest(AgentId)?.Status is null or "Completed" or "Failed");
        await Assert.That(File.Exists(Path.Combine(f.Checkout, "work.txt"))).IsTrue();
    }

    [Test]
    public async Task A_runtime_that_exits_inside_the_readiness_window_is_reported_and_leaves_nothing() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude") { ExitsLater = true }, readiness: TimeSpan.FromSeconds(5));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).Contains($"{ResumeInPlaceRefusals.Failed}: runtime_exited");
        await Assert.That(f.Factory.PtyRuntime!.HasExited).IsTrue();
        await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)).IsNull();
        await Assert.That(File.Exists(Path.Combine(f.Checkout, "work.txt"))).IsTrue();
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task A_refused_registration_reports_resume_failed() {
        var server = new CaptureServerConnection { RegistrationResponse = _ => new AgentRegisteredResponse(false, "not_yours") };
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"), server);

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).Contains($"{ResumeInPlaceRefusals.Failed}: registration_refused");
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest(AgentId)?.Status is null or "Completed" or "Failed");
        await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)).IsNull();
        await Assert.That(f.Orch.PidRecordsForTest().Select(r => r.AgentId)).DoesNotContain(AgentId);
    }

    /// <summary>Until the agent registers on the source only the lease keeps a hand-off off it, so a lease lost while
    /// the agent starts fails the resume.</summary>
    [Test]
    public async Task A_lease_lost_while_the_agent_starts_retires_it_and_reports_it() {
        var factory = new LoadableFactory("claude") { StartDelay = TimeSpan.FromMilliseconds(400) };
        var server  = new CaptureServerConnection { RenewHandoff = (_, _) => Task.FromResult(factory.Starts == 0) };
        await using var f = new Fixture(Tmp, factory, server, readiness: TimeSpan.FromSeconds(5));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsEquivalentTo([$"{ResumeInPlaceRefusals.Failed}: source_lease_lost"]);
        await Assert.That(f.Factory.PtyRuntime!.HasExited).IsTrue();
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest(AgentId)?.Status is null or "Completed" or "Failed");
        await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)).IsNull();
        await Assert.That(File.Exists(Path.Combine(f.Checkout, "work.txt"))).IsTrue();
    }

    /// <summary>A lease lost while the readiness window runs, after the agent started, still fails the resume.</summary>
    [Test]
    public async Task A_lease_lost_during_readiness_retires_the_agent() {
        var factory = new LoadableFactory("claude");
        DateTimeOffset? failFrom = null;
        var server = new CaptureServerConnection {
            RenewHandoff = (_, _) => {
                if (factory.Starts == 0) return Task.FromResult(true);
                failFrom ??= DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(300);

                return Task.FromResult(DateTimeOffset.UtcNow < failFrom);
            },
        };
        await using var f = new Fixture(Tmp, factory, server, readiness: TimeSpan.FromSeconds(3));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsEquivalentTo([$"{ResumeInPlaceRefusals.Failed}: source_lease_lost"]);
        await Assert.That(f.Factory.PtyRuntime!.HasExited).IsTrue();
    }

    /// <summary>A failure is reported only once the runtime is confirmed gone; one that will not stop stays registered
    /// on the source, which keeps a hand-off off it.</summary>
    [Test]
    public async Task A_failed_resume_that_cannot_be_stopped_is_not_reported() {
        var factory = new LoadableFactory("claude") { StartDelay = TimeSpan.FromMilliseconds(200), Unkillable = true };
        var server  = new CaptureServerConnection { RenewHandoff = (_, _) => Task.FromResult(factory.Starts == 0) };
        await using var f = new Fixture(Tmp, factory, server, readiness: TimeSpan.FromSeconds(3));

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsEmpty();
        await Assert.That(f.Factory.PtyRuntime!.HasExited).IsFalse();
        await Assert.That(f.Server.StatusChangedCalls.Where(c => c.AgentId == AgentId).Select(c => c.Status)).DoesNotContain("Failed");
        // Nothing was reported, so nothing may keep it from registering again after a reconnect.
        await Assert.That(f.Orch.GetAgentForTest(AgentId)!.LaunchFailureVerdictReported).IsEqualTo(0);
    }

    /// <summary>Consent judges the directory the resume runs in: a rule naming only the symlink does not admit its
    /// target.</summary>
    [Test]
    public async Task Consent_judges_where_a_symlinked_directory_resolves() {
        var link   = Tmp.PathTo("link");
        var policy = Tmp.CreateDir("consent");
        var store  = new LaunchConsentStore(policy, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeProvider.System);
        store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Deny, 5, [new LaunchConsentRule("allow", null, null, link, null)]), out _);
        var gate = new LaunchConsentGate(store, new LaunchConsentDecisionLog(policy, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance),
            prompter: null, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<LaunchConsentGate>.Instance);
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"), consent: gate);
        Directory.CreateSymbolicLink(link, f.Checkout);

        await f.Orch.HandleLaunchAgentForTest(f.Launch(directory: link));

        await Assert.That(f.Failures().Single()).StartsWith(LaunchConsentGate.DeniedReasonPrefix);
        await Assert.That(f.Factory.Starts).IsEqualTo(0);
    }

    /// <summary>Refused, the server holds no live agent on the source, so a runtime that will not die is not reported and
    /// keeps the lease until it exits.</summary>
    [Test]
    public async Task A_refused_registration_on_a_runtime_that_will_not_stop_keeps_the_lease_until_it_exits() {
        var server = new CaptureServerConnection { RegistrationResponse = _ => new AgentRegisteredResponse(false, "not_yours") };
        await using var f = new Fixture(Tmp, new LoadableFactory("claude") { Unkillable = true }, server);
        var alive = true;
        f.Orch.ProcessStillOurs = (_, _) => Volatile.Read(ref alive);

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsEmpty();
        await Assert.That(f.Server.HandoffEnds).IsEmpty();

        // A disposed runtime can call itself exited while its process lives; only the process ends the lease.
        f.Factory.PtyRuntime!.ExitGate.TrySetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        await Assert.That(f.Server.HandoffEnds).IsEmpty();

        Volatile.Write(ref alive, false);
        await WaitHarness.PollUntilAsync(() => f.Server.HandoffEnds.Contains(Token));
    }

    /// <summary>Cleanup kills once more, so a survivor it finishes off is reported like any refused resume.</summary>
    [Test]
    public async Task A_survivor_that_cleanup_kills_is_reported() {
        var server = new CaptureServerConnection { RegistrationResponse = _ => new AgentRegisteredResponse(false, "not_yours") };
        await using var f = new Fixture(Tmp, new LoadableFactory("claude") { Unkillable = true }, server);
        var asked = 0;
        f.Orch.ProcessStillOurs = (_, _) => Interlocked.Increment(ref asked) == 1;

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsEquivalentTo([$"{ResumeInPlaceRefusals.Failed}: registration_refused"]);
    }

    /// <summary>Only evidence that the process is still ours holds the lease: without it the refusal is reported and
    /// the lease ends, rather than renewing for a process that may be long gone.</summary>
    [Test]
    public async Task A_refused_registration_with_no_live_process_is_reported() {
        var server = new CaptureServerConnection { RegistrationResponse = _ => new AgentRegisteredResponse(false, "not_yours") };
        await using var f = new Fixture(Tmp, new LoadableFactory("claude") { Unkillable = true }, server);
        f.Orch.ProcessStillOurs = (_, _) => false;

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsEquivalentTo([$"{ResumeInPlaceRefusals.Failed}: registration_refused"]);
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    /// <summary>A failed renewal is no proof the server dropped the lease, so a survivor's lease is not ended because
    /// of one.</summary>
    [Test]
    public async Task A_survivors_lease_is_not_ended_by_a_failed_renewal() {
        var factory = new LoadableFactory("claude") { Unkillable = true };
        var server  = new CaptureServerConnection {
            RegistrationResponse = _ => new AgentRegisteredResponse(false, "not_yours"),
            RenewHandoff         = (_, _) => Task.FromResult(factory.Starts == 0),
        };
        await using var f = new Fixture(Tmp, factory, server);
        f.Orch.ProcessStillOurs = (_, _) => true;

        await f.Orch.HandleLaunchAgentForTest(f.Launch());
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        var renewed = f.Server.HandoffRenews.Count;
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        await Assert.That(f.Server.HandoffEnds).IsEmpty();
        // Renewing goes on past the failures, so the server-side lease does not lapse under the survivor.
        await Assert.That(f.Server.HandoffRenews.Count).IsGreaterThan(renewed);
    }

    /// <summary>The launch looks its factory up by the command's vendor exactly as sent, so a vendor no factory is keyed
    /// by is refused before anything starts, not lost to a lookup that throws.</summary>
    [Test]
    public async Task A_vendor_no_factory_is_keyed_by_is_refused() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"));

        await f.Orch.HandleLaunchAgentForTest(f.Launch() with { Vendor = "Claude" });

        await Assert.That(f.Failures()).IsEquivalentTo([ResumeInPlaceRefusals.UnsupportedVendor]);
        await Assert.That(f.Factory.Starts).IsEqualTo(0);
    }

    /// <summary>A token that lapsed while the command queued, before any renewal was due, starts nothing.</summary>
    [Test]
    public async Task A_lease_lapsed_before_the_launch_starts_nothing() {
        var server = new CaptureServerConnection { RenewHandoff = (_, _) => Task.FromResult(false) };
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"), server);

        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await Assert.That(f.Failures()).IsEquivalentTo([$"{ResumeInPlaceRefusals.Failed}: source_lease_lost"]);
        await Assert.That(f.Factory.Starts).IsEqualTo(0);
    }

    /// <summary>Ending the lease is best effort: a release that times out must not replace the launch's outcome.</summary>
    [Test]
    public async Task Ending_the_lease_never_throws() {
        var server = new CaptureServerConnection { EndHandoff = _ => Task.FromException(new OperationCanceledException()) };
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"), server);

        await f.Orch.KeepSourceLease(Token, Session).DisposeAsync();

        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    /// <summary>The directory is resolved again after consent and the runtime runs where it resolved, not at the
    /// symlink the session recorded, which could have been retargeted meanwhile.</summary>
    [Test]
    public async Task A_symlinked_directory_runs_where_it_resolves() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"));
        var link = Tmp.PathTo("link");
        Directory.CreateSymbolicLink(link, f.Checkout);

        await f.Orch.HandleLaunchAgentForTest(f.Launch(directory: link));

        await Assert.That(f.Failures()).IsEmpty();
        await Assert.That(f.Factory.Context!.Worktree.Path).IsEqualTo(f.Checkout);
        await Assert.That(f.Orch.ResumeLedger!.TryRead(AgentId)!.Launch.WorktreePath).IsEqualTo(f.Checkout);
    }

    [Test]
    [Arguments("borrowed")]
    [Arguments("resume-session")]
    [Arguments("review")]
    [Arguments("other-directory")]
    public async Task A_resume_block_on_another_launch_shape_is_refused(string shape) {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"));
        var plain = f.Launch();

        await f.Orch.HandleLaunchAgentForTest(shape switch {
            "borrowed"       => plain with { Borrowed = true, BorrowCwd = f.Checkout },
            "resume-session" => plain with { ResumeSessionId = Dashed },
            // Consent would judge a directory the runtime does not start in.
            "other-directory" => plain with { RepoPath = Tmp.CreateDir("elsewhere") },
            _                => plain with { Kind = LaunchKind.ReviewFlow },
        });

        await Assert.That(f.Failures().Single()).IsEqualTo(ResumeInPlaceRefusals.Invalid);
        await Assert.That(f.Factory.Starts).IsEqualTo(0);
        await Assert.That(f.Server.HandoffEnds.ToArray()).IsEquivalentTo([Token]);
    }

    [Test]
    public async Task A_launch_the_probe_would_refuse_is_refused_before_anything_starts() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"));

        await f.Orch.HandleLaunchAgentForTest(f.Launch(directory: Path.Combine(f.Checkout, "gone")));

        await Assert.That(f.Failures().Single()).IsEqualTo(ResumeInPlaceRefusals.DirectoryMissing);
        await Assert.That(f.Factory.Starts).IsEqualTo(0);
    }

    [Test]
    public async Task Stopping_a_resumed_agent_deletes_nothing() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"));
        await f.Orch.HandleLaunchAgentForTest(f.Launch());

        await f.Orch.HandleStopAgent(AgentId);
        await WaitHarness.PollUntilAsync(() => f.Orch.ResumeLedger!.TryRead(AgentId) is null);

        await Assert.That(File.Exists(Path.Combine(f.Checkout, "work.txt"))).IsTrue();
    }

    /// <summary>A daemon restart finds the in-place agent in its ledger: the startup sweep keeps its directory and the
    /// boot lane resumes it in place, never in a worktree of its own.</summary>
    [Test]
    public async Task An_in_place_agent_is_kept_by_the_sweep_and_resumed_in_place_after_a_restart() {
        await using var f = new Fixture(Tmp, new LoadableFactory("claude"));
        await f.Orch.HandleLaunchAgentForTest(f.Launch());
        var entry = f.Orch.ResumeLedger!.TryRead(AgentId)!;

        await using var restarted = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: f.Checkout, extraRuntimeFactories: [f.Factory],
            configure: c => {
                c.ResumeReadinessWindow = TimeSpan.FromMilliseconds(100);
                c.DefaultDirectories    = new Dictionary<HarnessId, string> { [HarnessId.Claude] = f.Store };
            });
        restarted.ResumeLedger!.Write(entry with { State = ResumeState.Resuming, ResumingAt = DateTimeOffset.UtcNow });

        restarted.LoadResumeLedger();
        await Assert.That(restarted.LedgerWorktreePaths()).Contains(f.Checkout);

        await restarted.ResumeOnBootAsync(CancellationToken.None);

        var ctx = f.Factory.Context!;
        await Assert.That((ctx.AgentId, ctx.Work, ctx.Worktree.Path, ctx.ResumeSessionId)).IsEqualTo((AgentId, WorkLocation.InPlace, f.Checkout, Dashed));
    }

    [Test]
    public async Task A_codex_in_place_resume_is_writable() =>
        await Assert.That(CodexPosturePolicy.Resolve(WorkLocation.InPlace, isReviewFlow: false, posture: null).Sandbox).IsEqualTo("workspace-write");

    [Test]
    public async Task A_week_old_codex_rollout_is_found_by_its_id() {
        var sessions = Tmp.CreateDir("codex", "sessions");
        var day      = Directory.CreateDirectory(Path.Combine(sessions, "2026", "10", "24")).FullName;
        var rollout  = Path.Combine(day, $"rollout-2026-10-24T09-00-00-{Dashed}.jsonl");
        File.WriteAllText(rollout, "{}\n");
        File.WriteAllText(Path.Combine(day, "rollout-2026-10-24T09-00-00-11111111-2222-3333-4444-555555555555.jsonl"), "{}\n");

        await Assert.That(CodexSessionRolloutLocator.TryLocateSession(sessions, Dashed)).IsEqualTo((Session, rollout));
        await Assert.That(CodexSessionRolloutLocator.TryLocateSession(sessions, "99999999-2222-3333-4444-555555555555")).IsNull();
    }
}
