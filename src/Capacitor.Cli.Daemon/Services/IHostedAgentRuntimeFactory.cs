using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// A runtime's unattended-hosting advertisement: whether it is offered, and — when a daemon-local
/// gate is what withholds it — the operator-actionable reason.
/// </summary>
/// <param name="Supported">What <see cref="IHostedAgentRuntimeFactory.SupportsUnattended"/> reports.</param>
/// <param name="WithheldReason">Non-null only when the vendor CAN host unattended agents and this
/// daemon is refusing to offer it. Null both when the vendor is offered and when it never claimed
/// unattended support in the first place.</param>
/// <param name="ProbeFailed">A support or version probe could not establish an answer, so classification may be retried.</param>
internal readonly record struct UnattendedSupport(bool Supported, string? WithheldReason, bool ProbeFailed = false);

/// <summary>
/// Runtime-selection seam, one implementation per vendor family, chosen by
/// <see cref="AgentOrchestrator.HandleLaunchAgent"/> from <c>cmd.Vendor</c>.
/// </summary>
internal interface IHostedAgentRuntimeFactory {
    /// <summary>Vendor token this factory handles ("claude", "codex", "cursor").</summary>
    string Vendor { get; }

    /// <summary>
    /// Reports whether this vendor's CLI resolves to an executable that looks installed. Used at
    /// daemon startup to build the vendor list advertised over <c>DaemonConnect</c>.
    /// </summary>
    bool IsAvailable();

    /// <summary>The binary a launch gets, whose <c>--version</c> identifies the build. Asked here so the
    /// advertised version and the admitted build cannot silently diverge.</summary>
    string CliPath { get; }

    /// <summary>
    /// Whether this vendor can host a fully UNATTENDED agent (<see cref="LaunchKind.ReviewFlow"/>).
    /// The orchestrator refuses an unattended launch for a vendor that returns <c>false</c>.
    /// </summary>
    bool SupportsUnattended { get; }

    /// <summary>
    /// <see cref="SupportsUnattended"/> and why it is withheld, in one evaluation, since gated reviewers
    /// spawn their binary to decide. The reason is set only when this daemon's configuration refuses a
    /// vendor that could host unattended agents.
    /// </summary>
    UnattendedSupport DescribeUnattendedSupport() => new(SupportsUnattended, null);

    /// <summary>Whether a <see cref="LaunchKind.Review"/> launch gets the <c>kcap mcp review</c> tools and
    /// review prompt here. A runtime without them would host a PR reviewer with no review surface.</summary>
    bool SupportsPrReview => false;

    /// <summary>Whether this runtime has a certified containment strategy for review flows that
    /// request the caller's current checkout contents.</summary>
    bool SupportsBorrowedReviewFlow => false;

    /// <summary>Stable protocol token naming the certified containment boundary.</summary>
    string? BorrowedReviewContainment => null;

    /// <summary>Whether a borrowed request must be materialized into an independent daemon-owned snapshot
    /// before this runtime is started.</summary>
    bool BorrowedReviewRequiresIndependentSnapshot => false;

    /// <summary>
    /// Whether a review-flow launch runs the vendor, and so its MCP children, under a <c>HOME</c> that is
    /// not the daemon user's. The <c>kcap-flow-result</c> channel finds its credential under <c>HOME</c>,
    /// so such a launch needs brokered delivery
    /// (<c>RuntimeStartContext.RequiresBrokeredResultDelivery</c>). Independent of borrowed-ness:
    /// Antigravity redirects <c>HOME</c> on every launch and refuses borrowed workspaces.
    /// </summary>
    bool ReviewFlowRedirectsHome => false;

    /// <summary>
    /// The reviewer model resolver, or null when the vendor has no authoritative one, which does not
    /// withdraw its unattended support. Advertised as
    /// <c>UnattendedVendorCapability.SupportsReviewerModelResolution</c>.
    /// </summary>
    IReviewerModelResolver? ReviewerModelResolver => null;

    /// <summary>
    /// Whether this runtime can apply a caller-supplied model. When false the model is discarded, so it
    /// must not be reported as running (<see cref="ModelSelectionLaunchPolicy"/>).
    /// </summary>
    bool SupportsModelSelection => true;

    /// <summary>Whether a restart resume — <see cref="RuntimeStartContext.ResumeSessionId"/> naming the
    /// vendor session, the prompt a nudge — continues that session here. Declared only once a live
    /// probe has passed for the vendor and transport.</summary>
    bool SupportsRestartResume(string? runtimeTransport) => false;

    /// <summary>Whether a flow-participant launch of this shape reopens its session across a daemon
    /// restart: the reviewer shape (its MCP config, HOME, snapshot) is not the interactive one. Declared
    /// only once a live probe of that exact shape has passed.</summary>
    bool SupportsReviewerRestartResume(string? runtimeTransport, WorkLocation work) => false;

    /// <summary>Whether a restart resume of an agent that was waiting for input can start with no prompt at all.
    /// Only a runtime that judges the resume by its process, not by a first turn, may say so.</summary>
    bool ResumesIdleWithoutPrompt(string? runtimeTransport) => false;

    /// <summary>Whether the vendor recorded background work that did not survive a restart, with no turn on it since.
    /// Asked after a resume with no prompt, because the vendor starts no turn on what it recorded.</summary>
    bool ReportsLostWork(string? transcriptPath) => false;

    /// <summary>The models this vendor can launch on this machine, or null when it publishes no
    /// catalog. Null is also every probe failure: consumers then fall through to their other sources
    /// instead of reading an empty list as "nothing usable".</summary>
    Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VendorModelOption>?>(null);

    /// <summary>The catalog of the account whose config directory is <paramref name="configDirectory"/>;
    /// null is the daemon's own default. A vendor whose catalog does not depend on the account answers
    /// its one catalog.</summary>
    Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(string? configDirectory, CancellationToken ct) =>
        ProbeModelsAsync(ct);

    /// <summary>Files whose fingerprint change means the catalog may have changed.</summary>
    IReadOnlyList<string> CatalogFingerprintPaths => [];

    /// <summary><see cref="CatalogFingerprintPaths"/> for the account whose config directory is
    /// <paramref name="configDirectory"/>; null is the daemon's own default.</summary>
    IReadOnlyList<string> CatalogFingerprintPathsFor(string? configDirectory) => CatalogFingerprintPaths;

    /// <summary>Whether <paramref name="content"/> of a catalog file describes the CLI this daemon probes.
    /// A file several builds of the vendor share is rewritten by each in turn, and another build's write
    /// says nothing about what this one launches, so it keeps the last fingerprint.</summary>
    bool CatalogWrittenByProbedCli(string path, byte[] content) => true;

    /// <summary>Where this runtime's attachments land for a launch of <paramref name="kind"/>. The
    /// question is whether the process can be running, write-contained, while a fetch for it happens:
    /// protected kinds refuse follow-ups, so their only fetch precedes the process and the worktree is
    /// safe for them.</summary>
    AttachmentPlacement AttachmentPlacementFor(LaunchKind kind) => AttachmentPlacement.Worktree;

    /// <summary>
    /// Prepares and starts the hosted runtime. Any exception, including
    /// <see cref="CodexHooksNotInstalledException"/>, becomes <c>LaunchFailed</c> with worktree cleanup.
    /// </summary>
    Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct);
}

/// <summary>Result of <see cref="IHostedAgentRuntimeFactory.StartAsync"/>. <paramref name="McpConfigPath"/>
/// is a temp file the orchestrator cleans up with the agent. <paramref name="Transcript"/> is the ACP
/// session and transcript to bind and forward, without downcasting <see cref="Runtime"/>; PTY factories
/// leave it null.</summary>
internal readonly record struct HostedRuntimeStart(IHostedAgentRuntime Runtime, string? McpConfigPath, IAcpTranscriptSource? Transcript = null);

/// <summary>
/// Everything a runtime factory needs to start a hosted agent for one launch, built after the
/// orchestrator's pre-flight guards have passed.
/// </summary>
/// <param name="CapacitorPath">The <c>kcap</c> binary, which <see cref="ReviewLaunchBuilder.BuildAsync"/> uses as the
/// review MCP server command. Never the vendor CLI path: only <c>kcap</c> has <c>mcp review</c>.</param>
internal sealed record RuntimeStartContext(
        string            AgentId,
        string            Vendor,
        string            SourceRepoPath,
        WorktreeInfo      Worktree,
        string?           Prompt,
        // Null when the runtime cannot apply a model; every launcher reads null as the vendor default.
        string?           Model,
        string?           Effort,
        string[]?         Tools,
        bool              IsReview,
        bool              IsReviewFlow,
        ReviewLaunchInfo? Review,
        ushort            Cols,
        ushort            Rows,
        string?           ServerUrl,
        string?           DaemonBridgeUrl,
        string            CapacitorPath,
        // The review-flow definition's MCP allowlist. PTY launchers write it into a temp mcp-config; the ACP
        // factory resolves it into extra session/new servers under per-launch wire names.
        string[]?         McpAllowlist = null,
        WorkLocation       Work = WorkLocation.OwnedWorktree,
        // Stamped into the child so a restarted daemon's OrphanReaper recognizes a recordless survivor from
        // a prior incarnation (same KCAP_DAEMON_ID, different epoch). Empty writes no markers.
        string             DaemonId    = "",
        string             DaemonEpoch = "",
        // This daemon's name, which selects its control socket among the daemons on the machine.
        string             DaemonName  = "",
        // Extra servers for an interactive ACP launch's session/new, sent after kcap's own set where the
        // descriptor injects one. Ignored by a review flow, whose list the factory builds, and by a
        // descriptor without SupportsMcpServers.
        IReadOnlyList<AcpMcpServerSpec>? McpServers = null,
        // True only when a borrowed request has been materialized into a fully independent,
        // daemon-owned repository snapshot. Factories use this to revalidate exact artifacts.
        bool               IsBorrowedSnapshot = false,
        // Exact loopback GET capability for the immutable Git-index review-context generation.
        // Present only for borrowed-snapshot review flows; never a backend or filesystem URL.
        string?            ReviewContextCapabilityUrl = null,
        // Exact loopback POST capability the result channel submits through, so a reviewer whose HOME
        // is not the daemon user's can report without a credential of its own. The daemon holds the
        // authenticated connection and forwards; nothing here is a backend URL.
        string?            FlowResultCapabilityUrl = null,
        // Whether the result channel must deliver through FlowResultCapabilityUrl; if so, a missing
        // capability fails the launch rather than falling back to KCAP_URL. Derived by the orchestrator from
        // both causes (a borrowed snapshot, ReviewFlowRedirectsHome), and the one thing AcpReviewFlowMcp reads.
        bool               RequiresBrokeredResultDelivery = false,
        // The model asked for and not applied, kept apart from Model so the user is told why.
        string?            DroppedModelPick = null,
        // Caller-selected Codex sandbox/approval posture, carried verbatim from
        // LaunchAgentCommand.CodexPosture through to LauncherContext.CodexPosture. Non-null only for
        // an interactive daemon-owned-worktree Codex launch that passed the orchestrator's guard.
        CodexLaunchPosture? CodexPosture = null,
        // The per-launch generated names (result-channel wire name, unmatchable MCP name). Not a caller
        // input: the factory overwrites it at the top of StartAsync. It lives here so one instance reaches
        // both the session/new list and the argv builder. Null on PTY launchers.
        LaunchIdentity?     LaunchIdentity = null,
        // The same clock the AgentInstance gets, handed over so an ACP factory wires it before StartAsync;
        // later, every SetLaunchStage would hit a null clock. Null for PTY launchers.
        AgentActivityClock? ActivityClock = null,
        // Resolved by the ACP factory, for non-review-flow launches only, onto the interaction bridge.
        string?             AcpPermissionPreset = null,
        // The Codex thread a parked reviewer relaunch reopens via thread/resume, suppressing the second
        // SessionStarted. Null for a fresh launch.
        string?             ResumeSessionId = null,
        string?             PermissionMode = null,
        // The same policy instance the AgentInstance carries, so factory and permission seam judge alike.
        // Null when no provider is wired or the build failed.
        PolicySnapshot?     PolicySnapshot = null,
        // The launch's transcript journal, unopened. An envelope-emitting factory opens it before
        // constructing its runtime and hands it to the constructor; a PTY factory ignores it.
        TranscriptJournal?  Journal = null,
        // The flow role the server named for a review-flow launch, exported as the participant's
        // role marker. Null for every other launch.
        string?             FlowRole = null,
        // A private agent: nothing in the child's environment may link its session to the server.
        bool                IsPrivate = false,
        // The vendor session id minted for a fresh interactive launch of a vendor that accepts one up
        // front. Null for every other launch.
        string?             PinnedSessionId = null,
        // The transport a resumed or continued agent ran on, from its ledger entry; a factory with
        // more than one transport starts it on that one. Null for a fresh launch.
        string?             RequestedTransport = null,
        // The transport the server will validate this launch's registration against. Null from an
        // older server. A ledger transport above outranks it.
        string?             ExpectedRuntimeTransport = null,
        // Which of the agent's user messages the server has stored, answered by its forwarder once bound.
        Func<IAcpPersistenceProbe?>? PersistenceProbe = null,
        // Null for a vendor without accounts.
        ResolvedAccount?    Account = null
    );
