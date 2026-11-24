using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Exercises <see cref="AcpHostedAgentRuntime"/>'s crash reconnect/resume (skip-whole-replay) against
/// <see cref="FakeAcpAgent"/> incarnations: the original child plus one fake per resume candidate,
/// spawned through the same <see cref="AcpReconnectSupport.Spawn"/> seam production uses. Attempt
/// backoffs are zero so tests are deterministic without a fake clock — nothing here waits on real
/// reconnect delays.
/// </summary>
public class AcpHostedAgentRuntimeReconnectTests {
    // Every use is a ceiling on something that must finish, never an assertion that it doesn't:
    // the suite's generous bound turns a starved run into a slow pass instead of a false failure.
    static readonly TimeSpan HangGuard = WaitHarness.AcpHangGuard;

    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int  Pid               { get; init; } = 4242;
        public bool HasExited         { get; private set; }
        public int? ExitCode          { get; private set; }
        public int  TerminateCalls    { get; private set; }
        public bool RefuseTermination { get; init; }

        public void SignalExited(int exitCode = 0) {
            HasExited = true;
            ExitCode  = exitCode;
            _exited.TrySetResult();
        }

        public async Task WaitForExitAsync(TimeSpan? timeout = null) {
            if (timeout is { } t)
                await Task.WhenAny(_exited.Task, Task.Delay(t)).ConfigureAwait(false);
            else
                await _exited.Task.ConfigureAwait(false);
        }

        public Task TerminateAsync(TimeSpan? timeout = null) {
            TerminateCalls++;
            if (!RefuseTermination)
                SignalExited();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Descendants listed per incarnation pid while that incarnation lives, as the OS lists none for a
    /// dead root; each stays alive until killed unless it refuses</summary>
    sealed class FakeProcessTree(List<string> events, Func<int, bool> rootAlive) {
        public readonly Dictionary<int, List<(int Pid, string Identity)>> DescendantsOf = [];
        public readonly HashSet<int> Unkillable = [];
        public bool FaultAfterFirstListing;
        readonly HashSet<int> _killed = [];
        readonly HashSet<int> _listed = [];

        public bool Listed(int pid) { lock (_listed) return _listed.Contains(pid); }

        public AcpProcessTreeProbe Probe => new(
            Descendants: pid => {
                if (!rootAlive(pid) || !DescendantsOf.TryGetValue(pid, out var listed)) return [];
                lock (_listed) {
                    if (FaultAfterFirstListing && _listed.Contains(pid)) throw new IOException("simulated process listing failure");
                    _listed.Add(pid);
                }
                return [.. listed];
            },
            Kill: (pid, _) => {
                lock (events) events.Add($"kill:{pid}");
                if (!Unkillable.Contains(pid)) lock (_killed) _killed.Add(pid);
            },
            IsAlive: (pid, _) => { lock (_killed) return !_killed.Contains(pid); });
    }

    sealed class Harness : IAsyncDisposable {
        public readonly List<(FakeAcpAgent Fake, FakeAcpProcess Process)> Incarnations = [];
        public readonly List<string> Events = [];
        public readonly FakeProcessTree Tree;
        readonly List<AcpEventEnvelope> Envelopes = [];
        public readonly List<int> RecordedPids = [];
        public int  ClearRecordCalls;
        public bool FailPidRecord;

        CancellationTokenSource Cts { get; } = new();
        public AcpHostedAgentRuntime Runtime { get; }
        AcpReconnectSupport?  Support { get; }

        /// <summary>Scripts a candidate BEFORE its read loop starts; index 1 = first candidate.</summary>
        public Action<FakeAcpAgent, int>? ConfigureCandidate { get; set; }

        readonly List<Task> _fakeLoops = [];
        readonly object     _envelopeLock = new();
        Task _envelopeDrain = Task.CompletedTask;

        public Harness(bool withSupport = true, bool refuseOriginalTermination = false, int maxResumes = 5) {
            var fake0 = new FakeAcpAgent();
            var proc0 = new FakeAcpProcess { Pid = 5000, RefuseTermination = refuseOriginalTermination };
            Incarnations.Add((fake0, proc0));
            Tree = new FakeProcessTree(Events, pid => {
                lock (Incarnations) return Incarnations.Any(i => i.Process.Pid == pid && !i.Process.HasExited);
            });

            Support = withSupport
                ? new AcpReconnectSupport {
                    Spawn                = SpawnCandidate,
                    TimeProvider         = TimeProvider.System,
                    AttemptDelays        = [TimeSpan.Zero, TimeSpan.Zero],
                    RetirementWait       = TimeSpan.FromSeconds(2),
                    SettlementWait       = TimeSpan.FromSeconds(5),
                    MaxResumesPerSession = maxResumes,
                    ProcessTree          = Tree.Probe
                }
                : null;

            if (Support is not null) {
                Support.PidCallbacks = new AcpPidRecordCallbacks(
                    Record: pid => {
                        if (FailPidRecord) throw new IOException("simulated PID record write failure");
                        RecordedPids.Add(pid);
                    },
                    Clear: () => ClearRecordCalls++);
            }

            var connection = new AcpConnection(fake0.ClientWriteStream, fake0.ClientReadStream, NullLogger.Instance);
            Runtime = new AcpHostedAgentRuntime(connection, proc0, NullLogger.Instance, TimeProvider.System, reconnect: Support);
        }

        (Stream Input, Stream Output, IAcpProcess Process) SpawnCandidate() {
            var fake = new FakeAcpAgent();
            var proc = new FakeAcpProcess { Pid = 5000 + Incarnations.Count };

            ConfigureCandidate?.Invoke(fake, Incarnations.Count);
            lock (Events) Events.Add($"spawn:{proc.Pid}");
            lock (Incarnations) Incarnations.Add((fake, proc));
            _fakeLoops.Add(fake.RunAsync(Cts.Token));

            return (fake.ClientWriteStream, fake.ClientReadStream, proc);
        }

        public int CandidateSpawns => Incarnations.Count - 1;

        public async Task StartAsync(string? initialPrompt = null) {
            _fakeLoops.Add(Incarnations[0].Fake.RunAsync(Cts.Token));
            _envelopeDrain = DrainEnvelopesAsync();

            await Runtime.StartAsync("/abs/worktree", initialPrompt, Cts.Token).WaitAsync(HangGuard);
        }

        async Task DrainEnvelopesAsync() {
            await foreach (var envelope in Runtime.Envelopes.ReadAllAsync(Cts.Token)) {
                lock (_envelopeLock) Envelopes.Add(envelope);
            }
        }

        public IReadOnlyList<AcpEventEnvelope> EnvelopeSnapshot() {
            lock (_envelopeLock) return Envelopes.ToArray();
        }

        public void CrashOriginal() {
            Incarnations[0].Fake.SimulateCrash();
            Incarnations[0].Process.SignalExited(1);
        }

        /// <summary>The finalize signal the orchestrator keys on: completes only when
        /// <c>ReadOutputAsync</c>'s enumerable ends (runtime logically terminal).</summary>
        public async Task RuntimeTerminalAsync() {
            await foreach (var _ in Runtime.ReadOutputAsync(Cts.Token)) { }
        }

        public static async Task PollUntilAsync(Func<bool> condition) {
            var deadline = DateTime.UtcNow + HangGuard;
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            await Assert.That(condition()).IsTrue();
        }

        public async ValueTask DisposeAsync() {
            Cts.Cancel();

            foreach (var loop in _fakeLoops) {
                try { await loop.WaitAsync(HangGuard); } catch { /* crashed/cancelled fakes */ }
            }
            try { await _envelopeDrain.WaitAsync(HangGuard); } catch { /* cancelled */ }

            await Runtime.DisposeAsync();

            foreach (var (fake, _) in Incarnations) {
                try { await fake.DisposeAsync(); } catch { /* a crashed fake may hold a dispatch fault */ }
            }

            Cts.Dispose();
        }
    }

    [Test]
    public async Task Crash_resumes_via_session_load_suppresses_replay_and_emits_system_note() {
        await using var h = new Harness();
        h.ConfigureCandidate = (fake, _) => fake.SetSessionLoadReplay([
            FakeAcpAgent.DefaultAgentMessageChunkUpdate(FakeAcpAgent.FixedSessionId, "replayed-one"),
            FakeAcpAgent.DefaultAgentMessageChunkUpdate(FakeAcpAgent.FixedSessionId, "replayed-two")
        ]);

        await h.StartAsync();

        h.CrashOriginal();

        var candidate = default(FakeAcpAgent);
        await Harness.PollUntilAsync(() => {
            if (h.CandidateSpawns == 0) return false;
            candidate = h.Incarnations[1].Fake;
            return candidate.ReceivedCalls.Any(c => c.Method == "session/load");
        });

        var load = candidate!.ReceivedCalls.Single(c => c.Method == "session/load");
        await Assert.That(load.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo(FakeAcpAgent.FixedSessionId);
        await Assert.That(load.Params!.Value.GetProperty("cwd").GetString()).IsEqualTo("/abs/worktree");

        // The resume is observable via the system_note; the replayed conversation must never
        // become envelopes (skip-whole-replay), and the note precedes any later envelope.
        await Harness.PollUntilAsync(() => h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote));

        var envelopes = h.EnvelopeSnapshot();
        await Assert.That(envelopes.Any(e => e.Text?.Contains("replayed-one") == true)).IsFalse();
        await Assert.That(envelopes.Any(e => e.Text?.Contains("replayed-two") == true)).IsFalse();

        // Idle crash — nothing was in flight, so no resend sentence.
        var note = envelopes.Single(e => e.Kind == AcpEventKind.SystemNote);
        await Assert.That(note.Text).IsEqualTo("Agent process restarted; the session was resumed.");

        // The resumed session is live: a new input reaches the CANDIDATE as session/prompt.
        await h.Runtime.SendUserInputAsync("after-resume").WaitAsync(HangGuard);
        await Harness.PollUntilAsync(() => candidate.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "after-resume"));

        // And the candidate's pid was durably recorded at spawn.
        await Assert.That(h.RecordedPids).Contains(5001);
    }

    /// <summary>A wrapper that dies alone leaves its re-execed agent reparented and alive. Retirement kills
    /// the descendants listed after the handshake before the candidate is spawned</summary>
    [Test]
    public async Task Wrapper_only_death_kills_the_reparented_agent_before_the_candidate_spawns() {
        await using var h = new Harness();
        h.Tree.DescendantsOf[5000] = [(6000, "start:6000")];

        await h.StartAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1);

        string[] events;
        lock (h.Events) events = [.. h.Events];
        await Assert.That(events).Contains("kill:6000");
        await Assert.That(Array.IndexOf(events, "kill:6000")).IsLessThan(Array.IndexOf(events, "spawn:5001"));
    }

    /// <summary>Listing spans the whole handshake, so an agent forked after initialize, during session/new, is
    /// still killed when its wrapper later dies alone</summary>
    [Test]
    public async Task Descendant_forked_during_session_new_is_killed_before_the_candidate_spawns() {
        await using var h = new Harness();
        var original = h.Incarnations[0].Fake;
        original.HoldSessionNewResponse = new TaskCompletionSource();

        var start = h.StartAsync();
        await Harness.PollUntilAsync(() => original.ReceivedCalls.Any(c => c.Method == "session/new"));
        h.Tree.DescendantsOf[5000] = [(6000, "start:6000")];
        await Harness.PollUntilAsync(() => h.Tree.Listed(5000));
        original.HoldSessionNewResponse.TrySetResult();
        await start;

        h.CrashOriginal();
        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1);

        string[] events;
        lock (h.Events) events = [.. h.Events];
        await Assert.That(Array.IndexOf(events, "kill:6000")).IsGreaterThanOrEqualTo(0);
        await Assert.That(Array.IndexOf(events, "kill:6000")).IsLessThan(Array.IndexOf(events, "spawn:5001"));
    }

    /// <summary>A launch whose wrapper dies mid-handshake fails to start, and disposing the runtime kills the agent
    /// it re-execed</summary>
    [Test]
    public async Task Failed_launch_kills_the_listed_agent_on_dispose() {
        await using var h = new Harness();
        var original = h.Incarnations[0].Fake;
        original.HoldSessionNewResponse = new TaskCompletionSource();
        h.Tree.DescendantsOf[5000] = [(6000, "start:6000")];

        var start = h.StartAsync();
        await Harness.PollUntilAsync(() => h.Tree.Listed(5000) && original.ReceivedCalls.Any(c => c.Method == "session/new"));
        h.CrashOriginal();

        try { await start; } catch { /* the launch fails; that is the precondition */ }
        await h.Runtime.DisposeAsync();

        string[] events;
        lock (h.Events) events = [.. h.Events];
        await Assert.That(events).Contains("kill:6000");
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
    }

    /// <summary>A listing that faults ends the poll early but neither fails the launch nor keeps what was already
    /// listed from being killed</summary>
    [Test]
    public async Task Faulted_listing_still_kills_what_was_listed() {
        await using var h = new Harness();
        h.Tree.DescendantsOf[5000] = [(6000, "start:6000")];
        h.Tree.FaultAfterFirstListing = true;

        await h.StartAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1);

        string[] events;
        lock (h.Events) events = [.. h.Events];
        await Assert.That(Array.IndexOf(events, "kill:6000")).IsGreaterThanOrEqualTo(0);
        await Assert.That(Array.IndexOf(events, "kill:6000")).IsLessThan(Array.IndexOf(events, "spawn:5001"));
    }

    /// <summary>A candidate whose wrapper dies during initialize fails its attempt, and the agent it re-execed,
    /// seen while the wrapper lived, is killed before the next candidate spawns</summary>
    [Test]
    public async Task Candidate_wrapper_death_during_initialize_kills_its_agent_before_the_next_spawn() {
        await using var h = new Harness();
        h.Tree.DescendantsOf[5001] = [(6001, "start:6001")];
        var hold = new TaskCompletionSource();
        h.ConfigureCandidate = (fake, index) => { if (index == 1) fake.HoldInitializeResponse = hold; };

        await h.StartAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.Tree.Listed(5001));
        h.Incarnations[1].Process.SignalExited(1);
        hold.TrySetResult();

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 2);

        string[] events;
        lock (h.Events) events = [.. h.Events];
        await Assert.That(events).Contains("kill:6001");
        await Assert.That(Array.IndexOf(events, "kill:6001")).IsLessThan(Array.IndexOf(events, "spawn:5002"));
    }

    /// <summary>A failed candidate whose agent survives its kill ends the incident: no further candidate may
    /// load the session beside it</summary>
    [Test]
    public async Task Failed_candidate_with_a_surviving_agent_finalizes_without_another_spawn() {
        await using var h = new Harness();
        h.Tree.DescendantsOf[5001] = [(6001, "start:6001")];
        h.Tree.Unkillable.Add(6001);
        var hold = new TaskCompletionSource();
        h.ConfigureCandidate = (fake, index) => { if (index == 1) fake.HoldInitializeResponse = hold; };

        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.Tree.Listed(5001));
        h.Incarnations[1].Process.SignalExited(1);
        hold.TrySetResult();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(1);
    }

    /// <summary>A descendant still alive after the retirement wait is an unconfirmed retirement: no candidate
    /// may load the session beside it</summary>
    [Test]
    public async Task Surviving_descendant_finalizes_without_spawning_a_candidate() {
        await using var h = new Harness();
        h.Tree.DescendantsOf[5000] = [(6000, "start:6000")];
        h.Tree.Unkillable.Add(6000);

        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();
        h.CrashOriginal();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
    }

    [Test]
    public async Task Crash_without_reconnect_support_finalizes_as_today() {
        await using var h = new Harness(withSupport: false);
        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();

        h.CrashOriginal();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
    }

    [Test]
    public async Task Crash_when_load_session_not_advertised_finalizes() {
        await using var h = new Harness();
        h.Incarnations[0].Fake.SetInitializeResult(FakeAcpAgent.BuildInitializeResult(1, loadSession: false));

        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();
        h.CrashOriginal();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
    }

    [Test]
    public async Task Queued_turn_parks_during_reconnect_and_delivers_after_the_note() {
        await using var h = new Harness();
        await h.StartAsync();

        var original = h.Incarnations[0].Fake;

        // Park a turn mid-flight on the original (response held forever), then queue another
        // behind it and crash. The queued turn must never be sent at the dead incarnation and must
        // arrive at the candidate strictly after the resume.
        original.HoldPromptResponses = new TaskCompletionSource();
        await h.Runtime.SendUserInputAsync("in-flight-turn").WaitAsync(HangGuard);
        await Harness.PollUntilAsync(() => original.ReceivedCalls.Any(c => c.Method == "session/prompt"));

        await h.Runtime.SendUserInputAsync("queued-turn").WaitAsync(HangGuard);

        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1 && h.Incarnations[1].Fake.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "queued-turn"));

        var candidate = h.Incarnations[1].Fake;

        // The queued turn went ONLY to the candidate…
        await Assert.That(original.ReceivedCalls.Count(c => c.Method == "session/prompt")).IsEqualTo(1);

        // …the interrupted turn was surfaced, never re-sent (at-most-once floor)…
        await Assert.That(candidate.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "in-flight-turn")).IsFalse();

        // …the note carries the resend sentence (a turn was in flight)…
        await Harness.PollUntilAsync(() => {
            var e = h.EnvelopeSnapshot();
            return e.Any(x => x.Kind == AcpEventKind.SystemNote)
                && e.Count(x => x.Kind == AcpEventKind.UserMessage) == 2;
        });

        var envelopes = h.EnvelopeSnapshot();
        var note = envelopes.Single(e => e.Kind == AcpEventKind.SystemNote);
        await Assert.That(note.Text!).Contains("resend");

        // …and each turn's UserMessage row was emitted exactly once, at accept time, in send order:
        // parking the queued turn during reconnect neither loses its row nor lets its later
        // re-admission duplicate it.
        var userMessages = envelopes.Where(e => e.Kind == AcpEventKind.UserMessage).Select(e => e.Text!).ToList();
        await Assert.That(userMessages).IsEquivalentTo(new[] { "in-flight-turn", "queued-turn" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Session_load_error_is_terminal_without_further_attempts() {
        await using var h = new Harness();
        h.ConfigureCandidate = (fake, _) =>
            fake.FailSessionLoad(-32603, "Failed to start session: Session is active in another process (PID 999)");

        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();
        h.CrashOriginal();

        await terminal.WaitAsync(HangGuard);

        // One candidate tried session/load and was refused; the refusal is terminal — no retry
        // (both measured refusal classes are durable).
        await Assert.That(h.CandidateSpawns).IsEqualTo(1);
        await Assert.That(h.ClearRecordCalls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Candidate_death_mid_handshake_fails_only_that_attempt() {
        await using var h = new Harness();
        h.ConfigureCandidate = (fake, index) => {
            if (index == 1) {
                // First candidate parks its initialize response; the test crashes it below.
                fake.HoldInitializeResponse = new TaskCompletionSource();
            }
        };

        await h.StartAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1 &&
            h.Incarnations[1].Fake.ReceivedCalls.Any(c => c.Method == "initialize"));

        // Kill candidate 1 mid-initialize: only that attempt fails; candidate 2 resumes.
        h.Incarnations[1].Fake.SimulateCrash();
        h.Incarnations[1].Process.SignalExited(1);

        await Harness.PollUntilAsync(() => h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote));
        await Assert.That(h.CandidateSpawns).IsEqualTo(2);
    }

    [Test]
    public async Task Pid_record_write_failure_fails_the_attempt_before_any_handshake() {
        await using var h = new Harness();
        h.FailPidRecord = true;

        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();
        h.CrashOriginal();

        await terminal.WaitAsync(HangGuard);

        // Every attempt spawned a candidate whose record write failed → disposed pre-handshake:
        // no candidate ever received a single frame.
        await Assert.That(h.CandidateSpawns).IsEqualTo(3);
        foreach (var (fake, _) in h.Incarnations.Skip(1))
            await Assert.That(fake.ReceivedCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Unconfirmed_corpse_exit_is_terminal_and_no_candidate_handshake_begins() {
        await using var h = new Harness(refuseOriginalTermination: true);
        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();

        // Pipe EOF without process exit — and the process then refuses termination, so retirement
        // cannot confirm the old tree is gone. session/load must never race a possibly-live prior
        // owner: the incident is terminal with zero candidates.
        h.Incarnations[0].Fake.SimulateCrash();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
        await Assert.That(h.Incarnations[0].Process.TerminateCalls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Resume_cap_finalizes_the_next_crash() {
        await using var h = new Harness(maxResumes: 1);
        await h.StartAsync();

        h.CrashOriginal();
        await Harness.PollUntilAsync(() => h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote));

        var terminal = h.RuntimeTerminalAsync();

        // The second crash exceeds the cap: finalize, no second resume.
        h.Incarnations[1].Fake.SimulateCrash();
        h.Incarnations[1].Process.SignalExited(1);

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(1);
    }

    [Test]
    public async Task Barrier_violating_late_update_after_reopen_is_emitted_as_live() {
        await using var h = new Harness();
        h.ConfigureCandidate = (fake, _) => fake.EmitConversationUpdateAfterLoadResponse = true;

        await h.StartAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote));

        // Pins the DOCUMENTED failure shape the per-vendor probe gate exists to exclude:
        // a conversation update arriving after the load response races the short
        // suppression tail. Whichever way the race lands, the gate machinery itself stays sound —
        // the note was emitted and the runtime resumed; the late update either fell inside the
        // suppression window (dropped) or after reopen (emitted as live text). Assert the
        // machinery outcome, not the race.
        await h.Runtime.SendUserInputAsync("still-alive").WaitAsync(HangGuard);
        await Harness.PollUntilAsync(() => h.Incarnations[1].Fake.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "still-alive"));
    }

    [Test]
    public async Task Terminate_during_reconnect_finalizes_once() {
        await using var h = new Harness();

        // A candidate that never answers initialize keeps the incident in-flight until the stop.
        h.ConfigureCandidate = (fake, _) => fake.HoldInitializeResponse = new TaskCompletionSource();

        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1);

        await h.Runtime.TerminateAsync(TimeSpan.FromSeconds(2)).WaitAsync(HangGuard);

        await terminal.WaitAsync(HangGuard);

        // Stop won: no resume happened (no system_note), and the incident unwound.
        await Assert.That(h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote)).IsFalse();
    }

    /// <summary>A turn parked by a reconnect that never finishes is never written, so a teardown must fault
    /// its write rather than leave whoever waits on it waiting for good.</summary>
    [Test]
    public async Task A_turn_parked_by_a_reconnect_faults_its_write_when_the_runtime_is_disposed() {
        await using var h = new Harness();
        h.ConfigureCandidate = (fake, _) => fake.HoldInitializeResponse = new TaskCompletionSource();
        await h.StartAsync();

        h.CrashOriginal();
        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1);
        var written = await h.Runtime.SendDispatchedInputAsync("parked", Guid.NewGuid()).WaitAsync(HangGuard);

        await h.Runtime.DisposeAsync();

        await Assert.That(async () => await written.WaitAsync(HangGuard)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Committed_successor_death_before_reopen_chains_into_the_same_incident() {
        await using var h = new Harness();

        var chained = false;
        h.Runtime.TestHookAfterCommit = () => {
            // Fires at the exact post-commit, pre-settlement instant. Kill the FIRST committed
            // successor only: its death must take the chained arm (installed stamp, still
            // Reconnecting, stamp ≠ lastHandledCrash), fold into the SAME incident, and the next
            // candidate must resume — one owner, one eventual note. The crash signal propagates
            // via EOF on another thread, so BLOCK here (owner thread, no lock held) until the
            // marker is observed — otherwise the owner can legally reach reopen first and the
            // death becomes a fresh incident, which is correct behavior but not the arm this test
            // exists to pin.
            if (!chained) {
                chained = true;
                h.Incarnations[1].Fake.SimulateCrash();
                h.Incarnations[1].Process.SignalExited(1);

                var deadline = DateTime.UtcNow + HangGuard;
                while (!h.Runtime.ChainedCrashPendingForTest && DateTime.UtcNow < deadline)
                    Thread.Sleep(5);
            }
        };

        await h.StartAsync();
        h.CrashOriginal();

        await Harness.PollUntilAsync(() => h.EnvelopeSnapshot().Any(e => e.Kind == AcpEventKind.SystemNote));

        // Candidate 1 committed then died (chained); candidate 2 resumed. Exactly one note — the
        // chained pass skipped its own note/reopen.
        await Assert.That(h.CandidateSpawns).IsEqualTo(2);
        await Assert.That(h.EnvelopeSnapshot().Count(e => e.Kind == AcpEventKind.SystemNote)).IsEqualTo(1);

        await h.Runtime.SendUserInputAsync("after-chained-resume").WaitAsync(HangGuard);
        await Harness.PollUntilAsync(() => h.Incarnations[2].Fake.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "after-chained-resume"));
    }

    /// <summary>A graceful stop closes the agent's stdin, so the agent exits on its own before any
    /// terminate: that exit is the stop, not a crash to resume.</summary>
    [Test]
    public async Task An_exit_after_a_graceful_stop_is_not_resumed() {
        await using var h = new Harness();
        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();

        await h.Runtime.RequestGracefulStopAsync().WaitAsync(HangGuard);
        h.Incarnations[0].Fake.SimulateCrash();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
    }

    [Test]
    public async Task Terminate_from_running_completes_the_finalize_signal() {
        await using var h = new Harness();
        await h.StartAsync();

        var terminal = h.RuntimeTerminalAsync();

        // A plain intentional stop of a HEALTHY session: the child exits, and the re-keyed
        // finalize signal must still fire (the pre-reconnect implementation had this implicitly by
        // waiting on process exit) — no reconnect incident, no candidates.
        await h.Runtime.TerminateAsync(TimeSpan.FromSeconds(2)).WaitAsync(HangGuard);
        h.Incarnations[0].Fake.SimulateCrash();

        await terminal.WaitAsync(HangGuard);
        await Assert.That(h.CandidateSpawns).IsEqualTo(0);
    }

    [Test]
    public async Task Held_turn_ack_resolves_on_resumed_delivery() {
        await using var h = new Harness();
        await h.StartAsync();

        h.CrashOriginal();

        // Enqueue while the incident is in flight: the turn parks (pre-gate admission) and its
        // write-ack resolves only on the successful post-resume write.
        var ack = h.Runtime.SendUserInputAndWaitForWriteAsync("parked-during-reconnect");

        await ack.WaitAsync(HangGuard);

        await Harness.PollUntilAsync(() => h.CandidateSpawns >= 1 && h.Incarnations[1].Fake.ReceivedCalls.Any(c =>
            c.Method == "session/prompt" &&
            c.Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString() == "parked-during-reconnect"));
    }
}
