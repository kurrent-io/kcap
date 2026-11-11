using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Daemon.Harness.Grok;
using Capacitor.Cli.Daemon.Harness.Kiro;
using Capacitor.Cli.Daemon.Harness.OpenCode;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>How an unattended ACP reviewer receives its validated stdio MCP servers.</summary>
internal enum AcpReviewFlowMcpTransport {
    /// <summary>Infer the transport from <see cref="AcpVendorDescriptor.SupportsMcpServers"/>.</summary>
    Default,
    /// <summary>The vendor cannot carry the reviewer's required result channel.</summary>
    Unsupported,
    /// <summary>Send the servers in ACP <c>session/new.mcpServers</c>.</summary>
    SessionNew,
    /// <summary>Preload the servers through Copilot CLI's <c>--additional-mcp-config</c>.</summary>
    CopilotAdditionalConfig
}

/// <summary>How an ACP vendor handles a server→client interaction frame during an unattended
/// review. <see cref="AutoApprove"/> preserves the existing Copilot behavior. <see cref="Fail"/>
/// is the stronger Cursor contract: Cursor's own launch flags must suppress interaction frames,
/// so receiving even one means that contract has regressed and the reviewer is reaped.</summary>
internal enum AcpUnattendedInteractionPolicy {
    Disabled,
    AutoApprove,
    Fail,

    /// <summary>Approve a frame that names only tools THIS launch injected; treat every other frame
    /// exactly as <see cref="Fail"/> does. The middle ground the other two cannot express:
    /// <see cref="AutoApprove"/> does not inspect the tool, and <see cref="Fail"/> assumes a correctly
    /// configured reviewer never raises a frame — measurably false on Kiro, which intermittently
    /// prompts for a tool that is in its own trust list. See <see cref="UnattendedToolAdmission"/>.
    /// </summary>
    AllowlistedAutoApprove
}

/// <summary>
/// These tokens route server behavior and are not interchangeable.
/// NativeToolClamp relies on the vendor's launch controls in the caller's checkout; a tool allowlist
/// can remove read capability as well as writes. IndependentSnapshot never exposes the caller's tree,
/// so its tool surface may remain readable. A snapshot launch must declare IndependentSnapshot,
/// and only platforms with a verified readable snapshot surface may advertise it.
/// </summary>
internal enum AcpBorrowedReviewContainment {
    None,
    NativeToolClamp,
    IndependentSnapshot
}

/// <summary>
/// <see cref="ModelSelector"/> alone determines model selection; a separate capability flag
/// would permit contradictory states. Custom selectors and test doubles are valid implementations.
/// Descriptors are shared across concurrent launches: argv collections must remain immutable.
/// Normalize default ImmutableArray values to Empty at construction so enumeration cannot fail later.
/// </summary>
internal sealed record AcpVendorDescriptor {
    /// <summary>Which harness this row hosts. Everything a launch needs to find in
    /// <see cref="DaemonConfig"/> — the binary, the default model — is keyed on it, so a row cannot
    /// read one vendor's slot while the boot binder writes another's.</summary>
    public HarnessId                   Harness                { get; }

    /// <summary>How this vendor is spelled outside this build. Derived, so the descriptor holds no
    /// second spelling that could drift from the one our payloads and ledgers carry.</summary>
    public string                      Vendor                 => Harness.VendorId;
    public ImmutableArray<string>      Argv                   { get; }
    public ImmutableArray<string>      UnattendedTrustArgv    { get; }

    /// <summary>Builds this vendor's unattended trust argv from the launch's OWN injected MCP specs,
    /// when a fixed <see cref="UnattendedTrustArgv"/> cannot express it. Mutually exclusive with it.
    ///
    /// <para>Null for every vendor whose trust argv is a constant. Present for Kiro, whose SCOPED
    /// trust list has to name the per-launch wire name of everything <c>session/new</c> injects — a
    /// fixed list would omit the review's allowlist servers, and under the <c>Fail</c> policy their
    /// first tool call would end the round.</para></summary>
    public Func<IReadOnlyList<Core.Acp.AcpMcpServerSpec>, LaunchIdentity, ImmutableArray<string>>?
                                       UnattendedTrustArgvBuilder { get; }
    public bool                        SupportsUnattended     { get; }
    public AcpUnattendedInteractionPolicy UnattendedInteractionPolicy { get; }
    public IAcpModelSelector           ModelSelector          { get; }
    public bool                        SupportsMcpServers     { get; }

    /// <summary>Whether an interactive launch is handed kcap's own MCP servers in <c>session/new</c>.
    /// Declared only for a vendor measured to run such a server in place of a same-named one from its own
    /// config: one that ran both would spawn every kcap server twice.</summary>
    public bool                        InjectsKcapMcpServers  { get; }
    public AcpReviewFlowMcpTransport   ReviewFlowMcpTransport { get; }
    public bool                        SupportsBorrowedReviewFlow { get; }
    public AcpBorrowedReviewContainment BorrowedReviewContainment { get; }

    /// <summary>
    /// Whether an INTERACTIVE hosted session of this vendor may attempt crash reconnect/resume
    /// (relaunch → <c>initialize</c> → <c>session/load</c>, replay suppressed). This is a
    /// PROBE-VERIFIED fact, never inferred from the vendor's advertised <c>loadSession</c>
    /// capability — all four registered vendors advertise it, and two measurably cannot honor it
    /// across a crashed owner (<c>docs/probes/2026-08-04-acp-reconnect-c0/</c>): Kiro refuses with a
    /// durable stale-owner lock ("Session is active in another process", identical at 0/15/60s), and
    /// Gemini answers session/load before most of its replay. Flipping a
    /// vendor to <see langword="true"/> requires a passing re-run of that probe, because the resume
    /// path additionally relies on the vendor honoring the ACP session/load response-after-replay
    /// barrier — a violation would duplicate transcript content. Runtime eligibility is conjunctive
    /// with the handshake's actual <c>loadSession</c> advertisement, an interactive (non-review)
    /// launch, and the <c>KCAP_ACP_RECONNECT</c> kill switch.
    /// </summary>
    public bool                        SupportsReconnectResume { get; }

    /// <summary>Whether a daemon restart may reopen this vendor's session with <c>session/load</c>
    /// instead of falling back to kcap continue. Set only for a vendor that passes the graceful-stop probe
    /// in <c>docs/probes/2026-10-19-acp-restart-resume/</c>: a vendor persists its session differently on a
    /// graceful stop than on a crash, so this is decided apart from <see cref="SupportsReconnectResume"/>.</summary>
    public bool                        SupportsRestartResume { get; }

    /// <summary>Builds the <c>_meta</c> sent on <c>session/new</c> for an interactive (false) or review
    /// (true) launch, fresh per launch so no launch can alter another's. Null sends none.</summary>
    public Func<bool, JsonObject>?      SessionNewMeta { get; }

    /// <summary>The vendor's admission rule under <see cref="AcpUnattendedInteractionPolicy.AllowlistedAutoApprove"/>;
    /// required by that policy and unused by every other.</summary>
    public UnattendedToolAdmitter?      ToolAdmitter { get; }

    /// <summary>Reads a turn's token usage from a notification other than <c>session/update</c>, for a
    /// vendor that reports it on its own extension method. Null for every vendor that does not.</summary>
    public Func<Core.Acp.AcpNotification, IReadOnlyList<Core.AcpEventEnvelope>>? TurnUsageReader { get; }

    /// <summary>Reads a subagent's start or finish from a vendor notification, for a vendor that runs
    /// subagents as child sessions on the same connection. Null for every vendor that does not, whose
    /// updates all belong to the hosted agent.</summary>
    public Func<Core.Acp.AcpNotification, AcpSubagentSignal?>? SubagentReader { get; }

    /// <summary>The vendor's own client method for asking the user questions, for a vendor that does not
    /// use ACP's <c>elicitation/create</c>. Null leaves such a request unhandled.</summary>
    public AcpQuestionProtocol? QuestionProtocol { get; }

    /// <summary>Whether this vendor's <c>session/request_permission</c> names its call by <c>toolCallId</c> alone,
    /// so the runtime fills in the title, kind and input the call announced. See <see cref="AcpAnnouncedToolCalls"/>.
    /// </summary>
    public bool                         CompletesPermissionToolCall { get; }

    public AcpVendorDescriptor(
            HarnessId                   Harness,
            ImmutableArray<string>      Argv,
            ImmutableArray<string>      UnattendedTrustArgv,
            bool                        SupportsUnattended,
            IAcpModelSelector           ModelSelector,
            bool                        SupportsMcpServers,
            AcpReviewFlowMcpTransport   ReviewFlowMcpTransport = AcpReviewFlowMcpTransport.Default,
            bool                        SupportsBorrowedReviewFlow = false,
            AcpUnattendedInteractionPolicy UnattendedInteractionPolicy = AcpUnattendedInteractionPolicy.Disabled,
            AcpBorrowedReviewContainment BorrowedReviewContainment = AcpBorrowedReviewContainment.None,
            bool                        SupportsReconnectResume = false,
            bool                        SupportsRestartResume = false,
            Func<IReadOnlyList<Core.Acp.AcpMcpServerSpec>, LaunchIdentity, ImmutableArray<string>>?
                                        UnattendedTrustArgvBuilder = null,
            Func<bool, JsonObject>?     SessionNewMeta = null,
            UnattendedToolAdmitter?     ToolAdmitter = null,
            Func<Core.Acp.AcpNotification, IReadOnlyList<Core.AcpEventEnvelope>>?
                                        TurnUsageReader = null,
            Func<Core.Acp.AcpNotification, AcpSubagentSignal?>?
                                        SubagentReader = null,
            AcpQuestionProtocol?        QuestionProtocol = null,
            bool                        CompletesPermissionToolCall = false,
            bool                        InjectsKcapMcpServers = false
        ) {
        // Before the checks below, every one of which names {Vendor} in its message: that is derived
        // from this, so reporting a malformed descriptor would otherwise accuse whichever vendor the
        // enum's default happens to be.
        this.Harness = Harness;

        var normalizedUnattendedTrustArgv = UnattendedTrustArgv.IsDefault ? ImmutableArray<string>.Empty : UnattendedTrustArgv;

        if (!SupportsUnattended && !normalizedUnattendedTrustArgv.IsEmpty)
            throw new ArgumentException(
                $"{nameof(UnattendedTrustArgv)} must be empty when {nameof(SupportsUnattended)} is false (vendor: {Vendor}).",
                nameof(UnattendedTrustArgv));

        if (!SupportsUnattended && UnattendedTrustArgvBuilder is not null)
            throw new ArgumentException(
                $"{nameof(UnattendedTrustArgvBuilder)} must be null when {nameof(SupportsUnattended)} is false (vendor: {Vendor}).",
                nameof(UnattendedTrustArgvBuilder));

        // Two sources for one argv is the ambiguity this rejects: a reader could not tell which wins,
        // and the answer would live in the factory rather than here.
        if (UnattendedTrustArgvBuilder is not null && !normalizedUnattendedTrustArgv.IsEmpty)
            throw new ArgumentException(
                $"{nameof(UnattendedTrustArgvBuilder)} and a non-empty {nameof(UnattendedTrustArgv)} are mutually exclusive (vendor: {Vendor}).",
                nameof(UnattendedTrustArgvBuilder));

        if (SupportsBorrowedReviewFlow && !SupportsUnattended)
            throw new ArgumentException(
                $"{nameof(SupportsBorrowedReviewFlow)} requires {nameof(SupportsUnattended)} (vendor: {Vendor}).",
                nameof(SupportsBorrowedReviewFlow));

        if (SupportsBorrowedReviewFlow && BorrowedReviewContainment == AcpBorrowedReviewContainment.None)
            throw new ArgumentException(
                $"{nameof(SupportsBorrowedReviewFlow)} requires an explicit borrowed-checkout containment boundary (vendor: {Vendor}).",
                nameof(BorrowedReviewContainment));

        if (!SupportsUnattended && UnattendedInteractionPolicy != AcpUnattendedInteractionPolicy.Disabled)
            throw new ArgumentException(
                $"{nameof(UnattendedInteractionPolicy)} must be Disabled when {nameof(SupportsUnattended)} is false (vendor: {Vendor}).",
                nameof(UnattendedInteractionPolicy));

        if (SupportsUnattended && UnattendedInteractionPolicy == AcpUnattendedInteractionPolicy.Disabled)
            throw new ArgumentException(
                $"{nameof(UnattendedInteractionPolicy)} must be explicit when {nameof(SupportsUnattended)} is true (vendor: {Vendor}).",
                nameof(UnattendedInteractionPolicy));

        this.Argv                = Argv.IsDefault ? ImmutableArray<string>.Empty : Argv;
        this.UnattendedTrustArgv = normalizedUnattendedTrustArgv;
        this.UnattendedTrustArgvBuilder = UnattendedTrustArgvBuilder;
        this.SupportsUnattended  = SupportsUnattended;
        this.UnattendedInteractionPolicy = UnattendedInteractionPolicy;
        this.ModelSelector       = ModelSelector;
        this.SupportsMcpServers  = SupportsMcpServers;
        this.InjectsKcapMcpServers = InjectsKcapMcpServers;
        this.SupportsBorrowedReviewFlow = SupportsBorrowedReviewFlow;
        this.BorrowedReviewContainment = BorrowedReviewContainment;
        this.SupportsReconnectResume = SupportsReconnectResume;
        this.SupportsRestartResume   = SupportsRestartResume;
        this.SessionNewMeta = SessionNewMeta;
        this.ToolAdmitter   = ToolAdmitter;
        this.TurnUsageReader = TurnUsageReader;
        this.SubagentReader  = SubagentReader;
        this.QuestionProtocol = QuestionProtocol;
        this.CompletesPermissionToolCall = CompletesPermissionToolCall;

        if (UnattendedInteractionPolicy == AcpUnattendedInteractionPolicy.AllowlistedAutoApprove && ToolAdmitter is null)
            throw new ArgumentException(
                $"{nameof(AcpUnattendedInteractionPolicy.AllowlistedAutoApprove)} requires a {nameof(ToolAdmitter)} (vendor: {Vendor}).",
                nameof(ToolAdmitter));
        this.ReviewFlowMcpTransport = ReviewFlowMcpTransport switch {
            AcpReviewFlowMcpTransport.Default when SupportsMcpServers => AcpReviewFlowMcpTransport.SessionNew,
            AcpReviewFlowMcpTransport.Default                         => AcpReviewFlowMcpTransport.Unsupported,
            _                                                         => ReviewFlowMcpTransport
        };

        if (this.ReviewFlowMcpTransport == AcpReviewFlowMcpTransport.SessionNew && !SupportsMcpServers)
            throw new ArgumentException(
                $"{nameof(AcpReviewFlowMcpTransport.SessionNew)} requires {nameof(SupportsMcpServers)} (vendor: {Vendor}).",
                nameof(ReviewFlowMcpTransport));

        if (InjectsKcapMcpServers && !SupportsMcpServers)
            throw new ArgumentException(
                $"{nameof(InjectsKcapMcpServers)} requires {nameof(SupportsMcpServers)} (vendor: {Vendor}).",
                nameof(InjectsKcapMcpServers));
    }
}

internal static class AcpVendorDescriptors {
    /// <summary>Cursor CLI's ACP hosted-agent surface. Review flows use Cursor's own unattended
    /// controls: <c>--force</c> suppresses command approval unless explicitly denied,
    /// <c>--approve-mcps</c> suppresses MCP-server approval, and <c>--trust</c> suppresses workspace
    /// trust. kcap does not auto-approve a fallback frame: any permission, elicitation, or unknown
    /// interaction request is a contract violation and reaps the reviewer.</summary>
    public static readonly AcpVendorDescriptor Cursor = new(
        Harness:             HarnessId.Cursor,
        Argv:                ["acp"],
        UnattendedTrustArgv: ["--force", "--approve-mcps", "--trust"],
        SupportsUnattended:  true,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  true,
        SupportsBorrowedReviewFlow: true,
        BorrowedReviewContainment: AcpBorrowedReviewContainment.IndependentSnapshot,
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail,
        // (docs/probes/2026-08-04-acp-reconnect-c0/): session/load works
        // across a SIGKILLed owner, the response-after-replay barrier holds, and a loaded session
        // prompts normally. Note the same probe found Cursor's replay REWRITES toolCallIds and
        // drops the interrupted turn — both irrelevant to the suppress-the-replay resume, but they
        // are why per-envelope replay matching stays impossible for this vendor.
        SupportsReconnectResume: true,
        // Ignores stdin EOF, so a restart always SIGKILLs it after the grace; session/load works across that.
        SupportsRestartResume: true
    );

    /// <summary>
    /// Copilot ignores stdio servers in session/new even though the shared capability advertisement
    /// cannot distinguish that behavior from vendors honoring them. Preload validated review servers
    /// through --additional-mcp-config and clamp their visible tools. Enabling session/new support needs
    /// a call-level verification, not an initialized connection or echoed capability flag.
    /// </summary>
    public static readonly AcpVendorDescriptor Copilot = new(
        Harness:             HarnessId.Copilot,
        Argv:                ["--acp", "--stdio"],
        UnattendedTrustArgv: ["--allow-all-tools", "--no-ask-user", "--no-custom-instructions", "--disable-builtin-mcps"],
        SupportsUnattended:  true,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  false,
        ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.CopilotAdditionalConfig,
        // Borrowed review is DELIBERATELY not declared here. Copilot's borrowed capability is
        // platform-resolved by CopilotBorrowedReviewPolicy, because the tool surface that makes a
        // borrowed snapshot both readable and contained has only been verified on some platforms.
        // Leaving a static declaration would put a second, always-stale answer in the codebase; a
        // reader who consulted it would get a containment token that no longer describes the launch.
        // If the platform special case were ever dropped, this default disables borrowed review
        // rather than silently permitting an unverified surface — the safe direction to fail.
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.AutoApprove,
        // (docs/probes/2026-08-04-acp-reconnect-c0/): session/load works
        // across a SIGKILLed owner, the barrier holds, toolCallIds are stable, and — unlike
        // Cursor — Copilot PERSISTS a mid-turn-killed prompt agent-side, which is why the
        // interrupted-turn disposition keys on local send facts, never on replay content.
        SupportsReconnectResume: true,
        SupportsRestartResume: true
    );

    /// <summary>
    /// Unattended launches suppress global MCP config with an empty, per-launch KIRO_HOME;
    /// the worktree layer removes branch-authored config. Trusted fs_read remains unrestricted
    /// by path, so this containment does not confine reads to the review worktree.
    /// Kiro accepts injected stdio MCP servers despite omitting stdio from its capability advertisement.
    /// Server initialization alone does not prove that a tool can be listed, trusted, and called.
    /// Model selection uses session/set_model; session/set_config_option is unsupported.
    /// Keep the vendor's default engine and model unless the caller requests an override.
    /// </summary>
    public static readonly AcpVendorDescriptor Kiro = new(
        Harness:             HarnessId.Kiro,
        Argv:                ["acp"],
        // Built PER LAUNCH from the same injected MCP specs and the same LaunchIdentity session/new
        // gets: a fixed list would omit the review's allowlist servers, and under the Fail policy
        // their first tool call would end the round. Never fs_write, never execute_bash.
        UnattendedTrustArgv:        [],
        UnattendedTrustArgvBuilder: KiroReviewerTrustList.BuildArgv,
        SupportsUnattended:  true,
        // AllowlistedAutoApprove, and the reason is measured rather than preferred.
        //
        // Fail was the original choice, on the premise that a scoped-trust reviewer emits no frame at
        // all. That premise is FALSE on kiro-cli 2.16.0: a live seeded-defect round raised a
        // session/request_permission for @kcap-flow-result/submit_review_result -- a tool that is in
        // this launch's own --trust-tools -- while an identical arm raised none. Under Fail that
        // intermittently reaps the reviewer on the very call that delivers its result.
        //
        // AutoApprove is not the alternative: it does not inspect the tool, so it would approve
        // exactly the out-of-surface request the scoping exists to reject. This policy approves only
        // the tools THIS launch injected and reaps anything else, which keeps the scoped posture --
        // still no fs_write, still no execute_bash -- while surviving the vendor's prompt leak.
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.AllowlistedAutoApprove,
        ToolAdmitter:        UnattendedToolAdmitter.Kiro,
        ModelSelector:       SetModelSelector.Instance,
        SupportsMcpServers:  true,
        ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.SessionNew,
        // (docs/probes/2026-08-04-acp-reconnect-c0/): Kiro advertises
        // loadSession but refuses session/load after a SIGKILLed owner with a DURABLE stale-owner
        // lock — "Failed to start session: Session is active in another process (PID <dead>)",
        // byte-identical at 0s/+15s/+45s, so bounded-backoff retries cannot clear it. Flip only
        // after a vendor fix AND a passing probe re-run.
        SupportsReconnectResume: false
    );

    /// <summary>
    /// OpenCode's ACP launch controls live in the environment, not global CLI flags.
    /// OPENCODE_PURE suppresses the capture plugin: without it, hosted sessions are ingested twice.
    /// <see cref="OpenCodeLaunchEnvironment"/> also isolates reviewer config and permissions.
    /// Models are read from configOptions[id=="model"].options and selected via session/set_config_option.
    /// Injected stdio MCP tools are supported despite the narrower capability advertisement.
    /// Denied tools are absent from the model's surface, so an interaction request indicates a
    /// broken launch contract and must fail the reviewer rather than approve additional access.
    /// <see cref="OpenCodeReviewerCapability"/> applies the operator opt-out and recorded build floor.
    /// Reconnect stays disabled until ownership after process death and replay completion are verified.
    /// </summary>
    public static readonly AcpVendorDescriptor OpenCode = new(
        Harness:             HarnessId.OpenCode,
        Argv:                ["acp"],
        // Empty, and not an oversight: OpenCode's trust vector is OPENCODE_PERMISSION, an env variable.
        // `opencode acp` accepts none of the global flags, so there is no argv form of it at all.
        UnattendedTrustArgv: [],
        SupportsUnattended:  true,
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  true,
        ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.SessionNew,
        // Borrowed review is deliberately NOT declared: nothing here has established a containment
        // boundary for a reviewer running in the caller's live checkout. The default fails closed to an
        // owned worktree, which is the safe direction.
        SupportsReconnectResume: false,
        SupportsRestartResume: true
    );

    /// <summary>
    /// Generate a nonempty, unguessable deny-all MCP name per launch. A repository can impersonate
    /// any fixed sentinel by declaring its own server with that name; an empty allowlist value fails
    /// Gemini's config load rather than proving containment.
    /// </summary>
    internal const string UnmatchableMcpNamePlaceholder = "__kcap_unmatchable_mcp_name__";

    /// <summary>Grok Build over <c>grok agent stdio</c> (<c>docs/probes/2026-10-13-grok-acp/</c>). Its
    /// MCP servers go through <c>session/new</c> and its model through <c>configOptions</c>, both
    /// verified at effect level.
    ///
    /// <para>An interactive launch is given kcap's own servers there. Grok runs a <c>session/new</c>
    /// server in place of the same-named one it imports from the user's Cursor config, so a registration left on a removed binary
    /// cannot take a kcap tool away from a hosted session. They need no session variable: Grok sets
    /// <c>GROK_SESSION_ID</c> on every server it starts.</para>
    ///
    /// <para><c>yoloMode: false</c> is not optional: Grok applies the user's own
    /// <c>permission_mode = "always-approve"</c> to an ACP session unless <c>session/new</c> overrides
    /// it, and then never sends <c>session/request_permission</c>, so neither the permission broker nor
    /// a launch preset would decide anything.</para>
    ///
    /// <para>As a reviewer it declares no containment, like Claude, so it reviews owned worktrees only. Of
    /// the frames Grok raises, only a call on a server this launch injected is approved, and the
    /// <see cref="GrokReviewerLaunch.AgentProfile"/> clamp removes the edit tools. That is a guard, not a
    /// boundary: the user's own Grok permission rules, hooks and MCP servers still act without asking,
    /// and no per-process switch turns them off short of moving <c>GROK_HOME</c>, which would mean
    /// handling the user's credential.</para>
    ///
    /// <para>Reconnect stays off: <c>session/load</c> worked from a fresh process after a SIGKILL, with
    /// stable tool-call ids, but the reconnect probe has not passed against Grok.</para></summary>
    public static readonly AcpVendorDescriptor Grok = new(
        Harness:             HarnessId.Grok,
        // A configured leader would hand the session to a shared process the daemon does not own.
        Argv:                ["agent", "--no-leader", "stdio"],
        UnattendedTrustArgv: [],
        SupportsUnattended:  true,
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.AllowlistedAutoApprove,
        ToolAdmitter:        GrokToolAdmission.Admitter,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  true,
        ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.SessionNew,
        SupportsReconnectResume: false,
        SessionNewMeta:      GrokReviewerLaunch.SessionNewMeta,
        TurnUsageReader:     GrokTurnUsage.Read,
        SubagentReader:      GrokSubagentSignals.Read,
        QuestionProtocol:    GrokQuestions.Protocol,
        InjectsKcapMcpServers: true
    );

    /// <summary>
    /// --skip-trust permits headless launches but does not remove inherited repository trust.
    /// Use unguessable per-launch MCP aliases so branch-authored servers cannot impersonate injected ones.
    /// The MCP allowlist must equal the injected server set: replace the comma-coerced option rather
    /// than append another occurrence, which would widen admission. Interactive launches inject no servers.
    /// Model-selection writes need effect-level verification before replacing NoOpModelSelector;
    /// a successful response or echoed model id alone cannot prove which model executes the turn.
    /// Keep the default approval mode: pinning plan mode would silently make interactive hosting read-only.
    /// </summary>
    public static readonly AcpVendorDescriptor Gemini = new(
        Harness:             HarnessId.Gemini,
        Argv:                ["--experimental-acp", "--skip-trust",
                              "--allowed-mcp-server-names", UnmatchableMcpNamePlaceholder],
        // --approval-mode yolo is REQUIRED for a reviewer, not a convenience: without it Gemini emits
        // session/request_permission before invoking its OWN injected result-channel tool, and no human is
        // there to answer, so the reviewer cannot report at all. Measured both ways. Review
        // launches only — an interactive hosted session must behave as the user's own does.
        UnattendedTrustArgv: ["--approval-mode", "yolo"],
        SupportsUnattended:  true,
        ModelSelector:       NoOpModelSelector.Instance,
        // Gemini honors session/new.mcpServers through tool listing and invocation, so this transport
        // carries the reviewer's result channel.
        SupportsMcpServers:  true,
        // Fail rather than AutoApprove: with --approval-mode yolo, Gemini emits NO interaction frame at all,
        // so receiving one means the launch contract regressed (a dropped flag, a vendor change) and the
        // honest response is to reap the reviewer rather than auto-approve whatever it asked for.
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail,
        // Ineligible: Gemini answers session/load before most of its replay, and replay suppression
        // ends once that response is in, so the rest would reach the transcript as live output.
        // Gemini also self-re-execs, so "the spawned pid exited" and "the agent died" are different
        // events.
        // TODO: enable once a gemini-cli release fixes google-gemini/gemini-cli#29707 and the probe
        // in docs/probes/2026-08-04-acp-reconnect-c0/ passes on it, with that release as a version
        // floor.
        SupportsReconnectResume: false
    );

    /// <summary>Mistral Vibe through <c>vibe-acp</c>, which takes no arguments. Interactive hosting only:
    /// no unattended reviewer has been measured, so none is offered. Its model is a standard
    /// <c>category: "model"</c> config option.</summary>
    public static readonly AcpVendorDescriptor MistralVibe = new(
        Harness:             HarnessId.MistralVibe,
        Argv:                [],
        UnattendedTrustArgv: [],
        SupportsUnattended:  false,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  true,
        // vibe-acp 3.0.1 sends a permission request's toolCall as {toolCallId} alone.
        CompletesPermissionToolCall: true,
        // Both resume probes pass on vibe-acp 3.0.1: session/load replays before its response after a
        // tree kill mid-turn and after a graceful stop, and the reloaded session recalls its history.
        SupportsReconnectResume: true,
        SupportsRestartResume:   true
    );
}
