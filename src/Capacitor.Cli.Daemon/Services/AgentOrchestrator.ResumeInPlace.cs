using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Recording;
using Capacitor.Cli.Core.Resume;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal partial class AgentOrchestrator {
    internal HarnessRegistry HarnessesForTest => _harnesses;

    /// <summary>Answers whether an ended session can be resumed here. A payload that does not parse is refused
    /// <c>resume_invalid</c> rather than answered with an Error frame.</summary>
    public async Task HandleLocalResumeProbeAsync(string payload, Stream stream, CancellationToken ct) {
        string? refusal;
        try {
            refusal = JsonSerializer.Deserialize(payload, ResumeInPlaceIpcJsonContext.Default.ResumeProbeRequestDto) is { } request
                ? await InPlaceResumeRefusalAsync(request.Vendor, request.SessionId, request.Directory, request.AccountId, request.ConfigDir)
                : ResumeInPlaceRefusals.Invalid;
        } catch (JsonException) {
            refusal = ResumeInPlaceRefusals.Invalid;
        }

        try {
            var json = JsonSerializer.Serialize(new ResumeProbeReplyDto(refusal), ResumeInPlaceIpcJsonContext.Default.ResumeProbeReplyDto);
            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.ResumeProbeReply) { Text = json }, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogDebug(ex, "ResumeProbe: the reply could not be written");
        }
    }

    /// <summary>Why the session cannot be resumed natively in <paramref name="directory"/> under the store it was
    /// recorded with, or null when it can. The probe and the launch run this same check.</summary>
    internal async Task<string?> InPlaceResumeRefusalAsync(string vendor, string sessionId, string directory, string? accountId, string? configDir) {
        if (NativeResumeId.For(vendor, sessionId) is null
         || !_runtimeFactories.TryGetValue(vendor.ToLowerInvariant(), out var factory)
         || !factory.SupportsRestartResume(null) || !factory.IsAvailable())
            return ResumeInPlaceRefusals.UnsupportedVendor;

        if (SessionId.Parse(SessionIds.Canonical(sessionId)) is { } session && HostsSession(session)) return ResumeInPlaceRefusals.SourceActive;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return ResumeInPlaceRefusals.DirectoryMissing;

        if (!(await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(directory)).Allowed) return ResumeInPlaceRefusals.DirectoryNotAllowed;

        return StoreRefusal(vendor, accountId, configDir);
    }

    /// <summary>The recorded store must be the one a resume runs under. An account vendor resolves it the way restart
    /// recovery does, keeping the recorded directory; with no registered account it must still be today's default,
    /// so a changed default is not resumed into. A vendor without accounts must still resolve its root to it.</summary>
    string? StoreRefusal(string vendorToken, string? accountId, string? configDir) {
        if (string.IsNullOrWhiteSpace(configDir) || HarnessId.From(vendorToken) is not { } vendor) return ResumeInPlaceRefusals.StoreChanged;

        try {
            if (!_config.DefaultDirectories.TryGetValue(vendor, out var defaultDirectory))
                return RecordingStore.Of(vendor, null, _harnesses) is { } root && AccountDirectory.Same(root, configDir) && Directory.Exists(configDir)
                    ? null
                    : ResumeInPlaceRefusals.StoreChanged;

            var resolved = AccountResolution.ResolveForResume(_config.Accounts?.TryLoad(), vendor, accountId, configDir, defaultDirectory);

            if (resolved.Account is not { } account || account.Directory != configDir) return ResumeInPlaceRefusals.StoreChanged;

            return account.AccountId is not null || AccountDirectory.Same(defaultDirectory, configDir) ? null : ResumeInPlaceRefusals.StoreChanged;
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) {
            return ResumeInPlaceRefusals.StoreChanged;
        }
    }
}

internal partial class AgentOrchestrator {
    internal TimeSpan SourceLeaseRenewal { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>The checks a resume launch runs before consent: a block the daemon cannot carry, and every reason the
    /// probe would have given.</summary>
    async Task<string?> InPlaceLaunchRefusalAsync(LaunchAgentCommand cmd, ResumeInPlaceLaunch inPlace) {
        if (cmd.Kind != LaunchKind.Default || cmd.Borrowed || cmd.Handoff is not null || cmd.ScratchWorkspace
         || !string.IsNullOrEmpty(cmd.ResumeSessionId) || string.IsNullOrWhiteSpace(cmd.Vendor)
         || string.IsNullOrWhiteSpace(inPlace.SourceSessionId) || string.IsNullOrWhiteSpace(inPlace.ReservationToken)
            // Consent judges the command's path, so the runtime may only start where consent looked.
         || !string.Equals(cmd.RepoPath, inPlace.Directory, StringComparison.Ordinal))
            return ResumeInPlaceRefusals.Invalid;

        // The launch looks its factory up by the command's own vendor, so the check must too.
        if (!_runtimeFactories.ContainsKey(cmd.Vendor)) return ResumeInPlaceRefusals.UnsupportedVendor;

        return await InPlaceResumeRefusalAsync(cmd.Vendor, inPlace.SourceSessionId, inPlace.Directory, inPlace.AccountId, inPlace.ConfigDir);
    }

    /// <summary>
    /// Resumes the source natively in its own directory through the restart-resume protocol: the runtime is bound to
    /// the original session from the start and judged by the readiness window, transcript included. A failure after
    /// the agent published retires it before <c>LaunchFailed</c> goes out, so the app's fallback cannot race it. Nothing
    /// is kept for a later resume: the app falls back to a hand-off, and the directory was never the daemon's.
    /// </summary>
    async Task<CommandOutcome> LaunchInPlaceAsync(
            LaunchAgentCommand cmd, ResumeInPlaceLaunch inPlace, SourceLeaseKeeper lease, string consented, LaunchConsentOutcome consent, LaunchHold hold,
            CancellationToken cancel, Action<AgentInstance>? onPublished) {
        // Re-resolved after consent, which can take minutes: a symlink retargeted meanwhile must not move the runtime
        // away from the directory consent judged.
        var authorized = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(inPlace.Directory);
        if (!authorized.Allowed || authorized.CanonicalCwd is not { } directory || directory != consented) {
            await _server.LaunchFailedAsync(cmd.AgentId, ResumeInPlaceRefusals.DirectoryNotAllowed);

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, cmd.AgentId, RejectReason: CommandRejectedReason.Semantic);
        }

        // The recorded store is checked again for the same reason: consent can outlast a rename or a retargeted link.
        if (StoreRefusal(cmd.Vendor, inPlace.AccountId, inPlace.ConfigDir) is { } storeRefusal) {
            await _server.LaunchFailedAsync(cmd.AgentId, storeRefusal);

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, cmd.AgentId, RejectReason: CommandRejectedReason.Semantic);
        }

        // Until the resumed agent registers on the source, only the lease keeps a hand-off from taking it over. Renewed
        // here rather than trusted: the token may have lapsed while the command queued, before any renewal was due.
        if (!await lease.ConfirmAsync()) {
            await _server.LaunchFailedAsync(cmd.AgentId, $"{ResumeInPlaceRefusals.Failed}: {SourceLeaseLostReason}");

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, cmd.AgentId, RejectReason: CommandRejectedReason.Semantic);
        }

        var factory = _runtimeFactories[cmd.Vendor];
        var native  = NativeResumeId.For(cmd.Vendor, inPlace.SourceSessionId)!;
        var entry   = InPlaceEntry(cmd, inPlace, directory, native, consent);
        var resume  = new ResumeLaunch(entry, WorktreeInfo.Borrowed(directory)) {
            Token         = cancel,
            StartsWaiting = string.IsNullOrWhiteSpace(cmd.Prompt) && factory.ResumesIdleWithoutPrompt(entry.Transport),
            KeepsNothing  = true,
        };
        var command = cmd with { RepoPath = directory, ResumeSessionId = native, ResumeInPlace = null, Prompt = cmd.Prompt ?? "" };

        var outcome = await LaunchAdmittedAsync(command, consent, hold, resume, cancel, onPublished);

        // Every failure before this point that reached LaunchAdmittedAsync's own paths was reported there.
        if (resume.RefusedRegistration is not null) {
            // Refused, the server holds no live agent on the source, so only the lease fences a runtime that will not die.
            if (resume.Survivor is { } survivor) {
                LogInPlaceRetireUnconfirmed(cmd.AgentId);
                lease.KeepUntilExit(survivor);

                return outcome;
            }

            await _server.LaunchFailedAsync(cmd.AgentId, $"{ResumeInPlaceRefusals.Failed}: {RegistrationRefusedReason}");

            return outcome;
        }

        // A launch that published and then failed, such as a process that exited at once, was reported there too.
        if (resume.Agent is not { } agent || outcome.Kind != CommandOutcomeKind.LaunchExecuted) return outcome;

        // Registered, so the server now refuses a takeover of the source while the agent lives; a lease lost before
        // then may already have let one in.
        var survived = !lease.IsLost && await SurvivesReadinessAsync(agent, _shutdownCts.Token, judgeTranscript: true) && !lease.IsLost;

        // A stop that came first ends the agent through its own teardown.
        if (agent.StopRequested || survived) return outcome;

        var reason = lease.IsLost && !agent.Runtime.HasExited ? SourceLeaseLostReason
                   : TranscriptRefused(agent)                 ? TranscriptRefusedReason
                   : "runtime_exited";

        LogInPlaceResumeFailed(cmd.AgentId, inPlace.SourceSessionId, reason);

        // Claimed before the retirement, so the exit's own verdict does not report the same failure a second time.
        var reports = Interlocked.CompareExchange(ref agent.LaunchFailureVerdictReported, 1, 0) == 0;

        agent.StopDetail ??= $"{ResumeInPlaceRefusals.Failed}: {reason}";

        // A failure reported while the runtime lives would let the app hand the session off beside it. Left running, it
        // stays registered on the source, which refuses a takeover, and remains the user's to stop.
        if (!await RetireAsync(agent)) {
            LogInPlaceRetireUnconfirmed(cmd.AgentId);
            // Nothing was reported, so nothing must stop it re-registering after a reconnect: that registration is what
            // keeps a takeover off the source.
            agent.StopDetail = null;
            if (reports) Volatile.Write(ref agent.LaunchFailureVerdictReported, 0);

            return outcome;
        }

        lock (_ledgerLock) DeleteLedger(cmd.AgentId);
        if (reports) await _server.LaunchFailedAsync(cmd.AgentId, $"{ResumeInPlaceRefusals.Failed}: {reason}");

        return new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, cmd.AgentId);
    }

    const string SourceLeaseLostReason = "source_lease_lost";

    /// <summary>Ends a failed resume's agent unless its own exit teardown already has.</summary>
    /// <summary>Stops the resumed agent; true only once its process is confirmed gone. It is killed before the stop
    /// path runs, because that path tells the server the agent stopped, which must not happen while it lives.</summary>
    async Task<bool> RetireAsync(AgentInstance agent) {
        try {
            if (!await TerminateConfirmedAsync(agent)) return false;

            if (_agents.TryGetValue(agent.Id, out var live) && ReferenceEquals(live, agent) && !agent.IsCleanupStarted)
                await StopAgentCoreAsync(agent);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogInPlaceRetireFailed(ex, agent.Id);
        }

        return agent.Runtime.HasExited;
    }

    /// <summary>A kill that throws is one that was not confirmed.</summary>
    async Task<bool> TerminateConfirmedAsync(AgentInstance agent) {
        try {
            if (!agent.Runtime.HasExited) await agent.Runtime.TerminateAsync(StopTerminateGrace);
            if (!agent.Runtime.HasExited) await agent.Runtime.WaitForExitAsync(StopExitPoll);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogCleanupStepFailed(ex, "terminating a resume", agent.Id);
        }

        return agent.Runtime.HasExited;
    }

    /// <summary>Whether the process still looks like this agent's, by the evidence cleanup's quarantine uses. Without
    /// a recorded identity nothing proves it is, so it is taken as gone.</summary>
    internal Func<int, string, bool> ProcessStillOurs { get; set; } =
        static (pid, identity) => ProcessIdentity.IsAlive(pid) && ProcessIdentity.MatchesTri(pid, identity) != false;

    ProcessEvidence? SurvivorOf(AgentInstance agent) =>
        !agent.Runtime.HasExited && agent.Runtime.Pid is > 0 and var pid && agent.StartIdentity is { } identity
     && ProcessStillOurs(pid, identity)
            ? new ProcessEvidence(pid, identity)
            : null;

    ResumeLedgerEntry InPlaceEntry(LaunchAgentCommand cmd, ResumeInPlaceLaunch inPlace, string directory, string native, LaunchConsentOutcome consent) {
        var now    = _time.GetUtcNow();
        var launch = new ResumeLaunchDescriptor(
            Prompt:           cmd.Prompt,
            Model:            cmd.Model,
            Effort:           cmd.Effort,
            RepoPath:         directory,
            WorktreePath:     directory,
            Work:             WorkLocation.InPlace,
            GitRelativeCwd:   null,
            SandboxPolicy:    cmd.CodexPosture?.Sandbox,
            ApprovalPolicy:   cmd.CodexPosture?.Approval,
            PermissionPreset: cmd.AcpPermissionPreset,
            McpConfigPath:    null,
            Title:            StartTitleOf(cmd)?.Text,
            TitleDerived:     StartTitleOf(cmd)?.Derived ?? true,
            IsPrivate:        false,
            Origin:           "server",
            RequesterUserId:  cmd.RequesterUserId,
            RequesterDisplay: cmd.RequesterDisplay,
            ConsentSource:    consent.Source,
            ConsentAt:        now.ToString("O"),
            FlowRunId:        null,
            FlowRole:         null,
            WorktreeOwnsBranch: false,
            PermissionMode:     cmd.PermissionMode,
            AccountDirectory:   inPlace.ConfigDir,
            AccountId:          inPlace.AccountId,
            ApprovalsReviewer:  cmd.CodexPosture?.ApprovalsReviewer);

        return NewRunningEntry(cmd.AgentId, cmd.Vendor, cmd.ExpectedRuntimeTransport ?? PreStartTransport,
            SessionIds.Canonical(inPlace.SourceSessionId), native, launch, prior: null) with {
            State = ResumeState.Resuming, ResumingAt = now,
        };
    }

    /// <summary>Renews the source's lease until disposed, then ends it, never throwing. A renewal that is refused, fails
    /// or goes unanswered loses the lease for good: the server may already have let it lapse.</summary>
    internal SourceLeaseKeeper KeepSourceLease(string token, string sourceSessionId) => new(this, token, sourceSessionId);

    internal sealed class SourceLeaseKeeper : IAsyncDisposable {
        readonly AgentOrchestrator       _owner;
        readonly string                  _token;
        readonly CancellationTokenSource _stop = new();
        readonly Task                    _loop;
        volatile bool                    _lost;

        public SourceLeaseKeeper(AgentOrchestrator owner, string token, string source) {
            _owner = owner;
            _token = token;
            _loop  = RenewAsync(source);
        }

        public bool IsLost => _lost;

        int _handedOff;

        /// <summary>Keeps renewing until <paramref name="process"/> exits, then ends the lease; disposing it before then
        /// does nothing.</summary>
        public void KeepUntilExit(ProcessEvidence process) {
            if (Interlocked.Exchange(ref _handedOff, 1) == 0) _ = EndOnExitAsync(process);
        }

        // A failed renewal is no proof the server dropped the lease, so only the process's exit ends it. Asked of the
        // process itself: a disposed runtime's HasExited says nothing about it.
        async Task EndOnExitAsync(ProcessEvidence process) {
            var renewedAt = _owner._time.GetTimestamp();

            try {
                while (_owner.ProcessStillOurs(process.Pid, process.Identity)) {
                    // The periodic loop stops at its first failed renewal; the survivor still needs the lease after it.
                    if (_lost && _owner._time.GetElapsedTime(renewedAt) >= _owner.SourceLeaseRenewal) {
                        await _owner.RenewHandoffLeaseOnceAsync(_token, RenewTimeout, _owner._shutdownCts.Token);
                        renewedAt = _owner._time.GetTimestamp();
                    }

                    await Task.Delay(ExitPoll < _owner.SourceLeaseRenewal ? ExitPoll : _owner.SourceLeaseRenewal, _owner._time,
                        _owner._shutdownCts.Token);
                }
            } catch (OperationCanceledException) {
                // The daemon is stopping with the runtime perhaps still alive: the lease lapses on its own, never ended early.
                await _stop.CancelAsync();

                return;
            }

            await EndAsync();
        }

        static readonly TimeSpan ExitPoll = TimeSpan.FromSeconds(1);

        /// <summary>Renews the lease now; false, and lost from then on, when the server no longer holds it.</summary>
        public async Task<bool> ConfirmAsync() {
            if (_lost) return false;
            if (await _owner.RenewHandoffLeaseOnceAsync(_token, RenewTimeout, _stop.Token)) return true;

            _lost = true;

            return false;
        }

        TimeSpan RenewTimeout => _owner.SourceLeaseRenewal / 2 < _owner.HandoffRenewTimeout ? _owner.SourceLeaseRenewal / 2 : _owner.HandoffRenewTimeout;

        async Task RenewAsync(string source) {
            var interval = _owner.SourceLeaseRenewal;
            var timeout  = RenewTimeout;

            try {
                while (true) {
                    await Task.Delay(interval, _owner._time, _stop.Token);
                    if (await _owner.RenewHandoffLeaseOnceAsync(_token, timeout, _stop.Token)) continue;
                    if (_stop.IsCancellationRequested) return;

                    _lost = true;
                    _owner.LogSourceLeaseLost(source);

                    return;
                }
            } catch (OperationCanceledException) {
                // Disposed.
            }
        }

        public ValueTask DisposeAsync() => Volatile.Read(ref _handedOff) == 1 ? ValueTask.CompletedTask : new(EndAsync());

        async Task EndAsync() {
            await _stop.CancelAsync();
            await _loop;
            _stop.Dispose();
            await _owner.EndHandoffLeaseAsync(_token);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "In-place resume: agent {AgentId} could not resume session {Session}: {Reason}")]
    partial void LogInPlaceResumeFailed(string agentId, string session, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "In-place resume: agent {AgentId} could not be retired")]
    partial void LogInPlaceRetireFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "In-place resume: agent {AgentId} failed but could not be confirmed stopped; left running")]
    partial void LogInPlaceRetireUnconfirmed(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "In-place resume: the lease on session {Session} was lost")]
    partial void LogSourceLeaseLost(string session);
}
