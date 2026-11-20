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
/// review. <see cref="Fail"/> is for a vendor whose launch flags suppress every frame, so one arriving
/// means the contract regressed and the reviewer is reaped.</summary>
internal enum AcpUnattendedInteractionPolicy {
    Disabled,
    AutoApprove,
    Fail,

    /// <summary>Approve a frame that names only tools this launch injected; fail every other frame.
    /// For a vendor that prompts for a trusted tool anyway, as Kiro intermittently does. See
    /// <see cref="UnattendedToolAdmission"/>.</summary>
    AllowlistedAutoApprove
}

/// <summary>The security boundary used to serve borrowed-checkout context without exposing the
/// caller's live checkout to an unattended reviewer. The server routes on this token, so each value's
/// promise matters:
/// <list type="bullet">
/// <item><b>NativeToolClamp</b>: the reviewer runs in the borrowed checkout and the vendor's own
/// launch flags keep it from writing there. The clamp that blocks writing may also block reading, so a
/// tool-allowlist clamp can leave a reviewer unable to see the code.</item>
/// <item><b>IndependentSnapshot</b>: the daemon materializes the authorized contents into its own
/// directory, so the filesystem is the boundary and the tools may stay readable.</item>
/// </list>
/// A snapshot-materialized launch handed to a vendor declaring anything but
/// <see cref="IndependentSnapshot"/> is a wiring bug and is rejected before spawn.</summary>
internal enum AcpBorrowedReviewContainment {
    None,
    NativeToolClamp,
    IndependentSnapshot
}

/// <summary>
/// Per-vendor wiring for AcpHostedAgentRuntimeFactory: which binary to spawn, the interactive and
/// unattended argv, and how the vendor's ACP surface handles model selection and MCP servers.
/// <see cref="ModelSelector"/> alone decides model selection; there is deliberately no boolean beside
/// it that could disagree. The explicit constructor normalizes a <c>default</c> argv array, which would
/// otherwise construct fine and throw on first enumeration.
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

    /// <summary>Builds the unattended trust argv from the launch's own injected MCP specs, for a vendor
    /// whose trust list must name each injected server's per-launch wire name (Kiro). Mutually exclusive
    /// with <see cref="UnattendedTrustArgv"/>.</summary>
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
    /// Whether an interactive hosted session may attempt crash reconnect (relaunch, <c>session/load</c>,
    /// replay suppressed). Probe-verified, never inferred from the <c>loadSession</c> advertisement:
    /// vendors advertise it and still fail across a crashed owner, and resume relies on the
    /// response-after-replay barrier, whose violation duplicates transcript content. Flip only on a
    /// passing run of <c>docs/probes/2026-08-04-acp-reconnect-c0/</c>.
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
        // Set first: every check below names {Vendor}, which derives from this.
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
        // Cursor's replay rewrites toolCallIds and drops the interrupted turn, so replay content cannot be
        // matched per envelope; the resume suppresses it instead.
        SupportsReconnectResume: true,
        // Ignores stdin EOF, so a restart always SIGKILLs it after the grace; session/load works across that.
        SupportsRestartResume: true
    );

    /// <summary>GitHub Copilot CLI as an ACP hosted agent (<c>copilot --acp --stdio</c>).
    /// <para><see cref="AcpVendorDescriptor.SupportsMcpServers"/> is false on a call-level probe: a stdio
    /// server passed in <c>session/new.mcpServers</c> is never spawned, while the same server preloaded
    /// through <c>--additional-mcp-config</c> is called. The <c>mcpCapabilities</c> advertisement cannot
    /// decide it; Kiro and Gemini advertise the same shape and honour stdio servers. Review flows
    /// therefore preload through <c>--additional-mcp-config</c>.</para></summary>
    public static readonly AcpVendorDescriptor Copilot = new(
        Harness:             HarnessId.Copilot,
        Argv:                ["--acp", "--stdio"],
        UnattendedTrustArgv: ["--allow-all-tools", "--no-ask-user", "--no-custom-instructions", "--disable-builtin-mcps"],
        SupportsUnattended:  true,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  false,
        ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.CopilotAdditionalConfig,
        // Borrowed review is resolved per platform by CopilotBorrowedReviewPolicy, not declared here, so
        // this default fails closed.
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.AutoApprove,
        // Copilot persists a mid-turn-killed prompt agent-side, so the interrupted-turn disposition keys
        // on local send facts, never on replay content.
        SupportsReconnectResume: true,
        SupportsRestartResume: true
    );

    /// <summary>AWS Kiro CLI as an ACP hosted agent (<c>kiro-cli acp</c>), interactive and as an
    /// unattended reviewer.
    /// <para>Unattended containment is source suppression: Kiro loads the operator's global
    /// <c>~/.kiro/settings/mcp.json</c> into every session, which would hand a reviewer
    /// <c>kcap-flows</c>, so a review runs with a daemon-owned empty <c>KIRO_HOME</c> and branch-authored
    /// workspace config is removed. A trusted <c>fs_read</c> is not path-scoped; that is an operator
    /// consent decision in <c>KiroReviewerCapability</c>.</para>
    /// <para>Kiro honours stdio <c>session/new</c> servers despite not advertising them, proven by a
    /// <c>tools/call</c> reaching the model. <c>server_initialized</c> alone would only prove the server
    /// started.</para>
    /// <para>Kiro rejects <c>session/set_config_option</c> but honours <c>session/set_model</c>, hence
    /// <see cref="SetModelSelector"/>. <c>--agent-engine</c> is not pinned, so hosted sessions match
    /// interactive ones.</para></summary>
    public static readonly AcpVendorDescriptor Kiro = new(
        Harness:             HarnessId.Kiro,
        Argv:                ["acp"],
        // Never fs_write, never execute_bash.
        UnattendedTrustArgv:        [],
        UnattendedTrustArgvBuilder: KiroReviewerTrustList.BuildArgv,
        SupportsUnattended:  true,
        // kiro-cli 2.16.0 intermittently asks permission for a tool in its own --trust-tools, so Fail would
        // reap the reviewer on the call that delivers its result, and AutoApprove would approve
        // out-of-surface requests too.
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.AllowlistedAutoApprove,
        ToolAdmitter:        UnattendedToolAdmitter.Kiro,
        ModelSelector:       SetModelSelector.Instance,
        SupportsMcpServers:  true,
        ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.SessionNew,
        // Ineligible: after a SIGKILLed owner, session/load fails on a durable stale-owner lock ("Session is
        // active in another process") that no retry clears.
        SupportsReconnectResume: false
    );

    /// <summary>SST OpenCode as an ACP hosted agent (<c>opencode acp</c>), interactive and as an
    /// unattended reviewer.
    /// <para>Models come from <c>session/new</c>'s <c>configOptions[id=="model"]</c>, with no
    /// <c>models</c> object; <c>session/set_config_option</c> was verified to take effect.</para>
    /// <para><c>opencode acp</c> accepts no global flags, so every launch control is an environment
    /// variable (<see cref="OpenCodeLaunchEnvironment"/>). <c>OPENCODE_PURE=1</c> is required on every
    /// hosted launch: it suppresses kcap's own live-ingest plugin in the child, which would otherwise
    /// ingest the session twice.</para>
    /// <para><see cref="AcpUnattendedInteractionPolicy.Fail"/> because a denied tool is absent from the
    /// model's surface rather than refused, so a correctly configured reviewer raises no frame at all.
    /// One arriving means the launch contract regressed.</para>
    /// <para>Reconnect is unprobed, not measured ineligible.</para></summary>
    public static readonly AcpVendorDescriptor OpenCode = new(
        Harness:             HarnessId.OpenCode,
        Argv:                ["acp"],
        // The trust vector is OPENCODE_PERMISSION; `opencode acp` has no argv form of it.
        UnattendedTrustArgv: [],
        SupportsUnattended:  true,
        UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail,
        ModelSelector:       ConfigOptionModelSelector.Instance,
        SupportsMcpServers:  true,
        ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.SessionNew,
        // No containment boundary is established for the live checkout, so borrowed review fails closed.
        SupportsReconnectResume: false,
        SupportsRestartResume: true
    );

    /// <summary>
    /// Replaced per launch with an unguessable name, so an MCP allowlist can deny everything without a
    /// literal a repository's <c>.gemini/settings.json</c> could name its server after. It must be
    /// non-empty: <c>--allowed-mcp-server-names ""</c> fails Gemini's config load before the session starts.
    /// </summary>
    internal const string UnmatchableMcpNamePlaceholder = "__kcap_unmatchable_mcp_name__";

    /// <summary>Grok Build over <c>grok agent stdio</c>. MCP servers go through <c>session/new</c> and the
    /// model through <c>configOptions</c>.
    /// <para>An interactive launch is given kcap's own servers: Grok runs a <c>session/new</c> server in
    /// place of the same-named one it imports from the user's Cursor config, and sets
    /// <c>GROK_SESSION_ID</c> on every server it starts.</para>
    /// <para><c>yoloMode: false</c> is required: otherwise Grok applies the user's
    /// <c>permission_mode = "always-approve"</c> and never sends <c>session/request_permission</c>.</para>
    /// <para>As a reviewer it declares no containment and reviews owned worktrees only. The admitter and
    /// the <see cref="GrokReviewerLaunch.AgentProfile"/> clamp are a guard, not a boundary: the user's own
    /// Grok rules, hooks and MCP servers still act.</para></summary>
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

    /// <summary>Google Gemini CLI as an ACP hosted agent (<c>gemini --experimental-acp</c>), interactive
    /// and as an unattended reviewer.
    /// <para><c>--skip-trust</c> is required, not containment: Gemini exits 55 in an untrusted directory,
    /// and trust inherits from a trusted parent, which a daemon worktree inside the operator's repository
    /// has. Under that inherited trust a repository-authored <c>.gemini/settings.json</c> MCP server
    /// starts as the daemon user, so the allowlist denies everything by default
    /// (<see cref="UnmatchableMcpNamePlaceholder"/>).</para>
    /// <para>A review launch replaces the placeholder with the comma-joined per-launch aliases of the
    /// servers it injects, never appending a second option, which would widen the gate. An interactive
    /// caller that starts populating <c>RuntimeStartContext.McpServers</c> must widen the gate too.</para>
    /// <para><see cref="NoOpModelSelector"/> until a selector's write is verified to take effect: both wire
    /// selectors fail silently, reporting the requested model while running another.</para>
    /// <para><c>--approval-mode</c> is not passed interactively, so hosted Gemini behaves as the user's
    /// own session does.</para></summary>
    public static readonly AcpVendorDescriptor Gemini = new(
        Harness:             HarnessId.Gemini,
        Argv:                ["--experimental-acp", "--skip-trust",
                              "--allowed-mcp-server-names", UnmatchableMcpNamePlaceholder],
        // Without yolo, Gemini asks permission before calling its own injected result-channel tool, and no
        // one is there to answer.
        UnattendedTrustArgv: ["--approval-mode", "yolo"],
        SupportsUnattended:  true,
        ModelSelector:       NoOpModelSelector.Instance,
        SupportsMcpServers:  true,
        // Under yolo Gemini raises no frame, so one arriving means the launch contract regressed.
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
