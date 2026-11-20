using System.Collections.Frozen;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Daemon;

public class DaemonConfig {
    public string   Name                { get; set; } = "";
    public string   ServerUrl           { get; set; } = "";
    public string[] AllowedRepoPaths    { get; set; } = [];
    public int      MaxConcurrentAgents { get; set; } = DaemonSettings.DefaultMaxAgents;

    /// <summary>The path this daemon's binary was started from, read at process start. The restart poll watches it and
    /// a self-respawn runs it: on Windows <c>kcap update</c> moves the running image elsewhere, so the process's own
    /// path cannot be trusted to name the install afterwards.</summary>
    public string? LaunchPath { get; set; }

    /// <summary>The daemon's own binary as it stood at process start, the restart poll's first baseline.</summary>
    public BinaryStat? LaunchBinary { get; set; }

    /// <summary>How long a self-detected restart waits for interactive agents to reach a turn boundary.</summary>
    public TimeSpan RestartTurnWait { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How many ledgered agents a booting daemon relaunches at once.</summary>
    public int ResumeConcurrency { get; set; } = 2;

    /// <summary>A ledgered agent last alive longer ago than this is interrupted rather than resumed.</summary>
    public TimeSpan ResumeMaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Whether a booting daemon resumes its ledgered agents at all.</summary>
    public bool AutoResume { get; set; } = true;

    /// <summary>How long a resumed runtime must stay alive after publishing for the resume to count;
    /// for a PTY vendor this is the whole handshake.</summary>
    public TimeSpan ResumeReadinessWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The <c>kcap</c> CLI binary beside the daemon (inside the daemon, <see cref="Environment.ProcessPath"/>
    /// is <c>kcap-daemon</c>), used to recognize an absolute-path kcap MCP entry (as in <see cref="ClaudeLauncher"/>'s
/// worktree merge). Null when no sibling
    /// resolves; then only the literal <c>"kcap"</c> is recognized.
    /// </summary>
    public string? KcapCliPath { get; set; } = Core.Mcp.KcapBinaryCommand.ResolveCliSibling();

    /// <summary>
    /// Heartbeat backstops for a review-flow reviewer whose run ended on the server without the daemon
    /// hearing, so it cannot hold a slot forever. Zero disables a bound. Env
    /// <c>KCAP_REVIEWER_MAX_LIFETIME</c>/<c>KCAP_REVIEWER_IDLE_TIMEOUT</c> (seconds). Interactive agents
    /// are never affected.
    /// </summary>
    public TimeSpan ReviewerMaxLifetime { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan ReviewerIdleTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How long a resumable reviewer (app-server Codex) may idle between rounds before it is
    /// parked: the slot is freed and the thread kept for resume.</summary>
    public TimeSpan ReviewerResumableIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a reviewer's turn may stay in flight with no activity before it is reaped as
    /// <c>turn_wedged</c>. A local safety net, deliberately not taken from the server. Zero disables it;
    /// env <c>KCAP_REVIEWER_TURN_WEDGE_CEILING</c> (seconds).
    /// </summary>
    public TimeSpan ReviewerTurnWedgeCeiling { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>Where this daemon's files live.</summary>
    public DaemonStore Store {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.Store was never set; pass a {nameof(DaemonStore)} in from the entry point.");
        set;
    }

    /// <summary>Where this daemon's kcap configuration lives — a different anchor from
    /// <see cref="Store"/>, which ignores <c>KCAP_CONFIG_DIR</c> by design.</summary>
    public ConfigRoot ConfigRoot {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.ConfigRoot was never set; pass a {nameof(Core.ConfigRoot)} in from the entry point.");
        set;
    }

    /// <summary>The local installation's root, so every transport to the server is checked against its registry.</summary>
    public LocalRoot? LocalRoot { get; set; }

    /// <summary>The profile this daemon resolved at boot. Nothing re-resolves it, so its token reads and
    /// daemon identity name one profile for the daemon's whole life.</summary>
    public ProfileContext Profiles {
        get => _profiles ?? throw new InvalidOperationException($"DaemonConfig.Profiles was never set; pass a {nameof(ProfileContext)} in from the entry point.");
        set => _profiles = value;
    }

    ProfileContext? _profiles;

    /// <summary>The profile name, or null where no <see cref="ProfileContext"/> was passed in.</summary>
    public string? ProfileNameOrNull => _profiles?.Name;

    /// <summary>Why <see cref="ServerUrl"/> may not be sent to, as resolved at boot; null when it may, or when nothing
    /// was resolved.</summary>
    public string? DestinationRefusal => _profiles is null ? null : LocalDestination.Refusal(ServerUrl, _profiles.Resolution.Endpoint);

    /// <summary>Whether the resolved profile opted out of the update check; false when nothing was resolved.</summary>
    public bool UpdateCheckOff => _profiles?.Effective?.UpdateCheck == false;

    /// <summary>A per-boot GUID written into each child's <c>KCAP_DAEMON_EPOCH</c>, so the startup scan can
    /// kill this daemon's survivors from a prior boot. <c>DaemonRunner</c> pins it before building services
    /// so the advertised epoch and the orchestrator's agree; null lets the orchestrator generate one.</summary>
    public string? DaemonEpoch { get; set; }

    /// <summary>
    /// Whether OS containment leaves no recordless survivor class (the Windows Job Object), recorded before
    /// any connect or spawn and advertised on <c>DaemonConnect</c>. False makes the server require per-id
    /// death proof; a failed record fails closed to false.
    /// </summary>
    public bool RecordlessSurvivorsImpossible { get; set; }

    /// <summary>
    /// Per-process GUID, also in the flock file, sent over <c>DaemonConnect</c> so the server can tell a
    /// reconnect from a different daemon claiming the same name.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>The instance of the boot just before this one, as the daemon lock witnessed it; null when
    /// the lock could not tell. A flow participant resumes only from that instance's planned stop.</summary>
    public string? PriorInstanceId { get; set; }

    /// <summary>The daemon's <c>AssemblyInformationalVersion</c>, sent over <c>DaemonConnect</c>.</summary>
    public string? Version { get; set; }

    /// <summary>Installed vendors, sent over <c>DaemonConnect</c> so the launch dialog offers only
    /// these.</summary>
    public string[]? SupportedVendors { get; set; }

    /// <summary>The installed vendors that can run unattended, so the server gates a reviewer-vendor
    /// override on capability, not just installation.</summary>
    public string[]? UnattendedVendors { get; set; }

    internal bool StartupUnattendedClassificationPending { get; set; }

    /// <summary>Setting this keeps the Codex resolution already paired with the capabilities; a caller
    /// that computed them from a resolution publishes both through <see cref="Advertise"/>.</summary>
    public IReadOnlyList<UnattendedVendorCapability>? UnattendedVendorCapabilities {
        get => Advertisement.Capabilities;
        set => Advertisement = Advertisement with { Capabilities = value };
    }

    internal UnattendedAdvertisement Advertisement {
        get => Volatile.Read(ref field);
        private set => Volatile.Write(ref field, value);
    } = UnattendedAdvertisement.None;

    internal void Advertise(IReadOnlyList<UnattendedVendorCapability> capabilities, Harness.Codex.CodexTransportResolution codex) =>
        Advertisement = new(capabilities, codex);

    /// <summary>Installed vendors whose runtime can host a single-pass PR review — the server offers and
    /// admits only these for one. <c>null</c> before the runner has probed.</summary>
    public string[]? PrReviewVendors { get; set; }

    /// <summary>Per-vendor fingerprint of the binary <see cref="UnattendedVendorCapabilities"/> was
    /// probed from, taken before that probe; the vendor CLI watcher's starting point.</summary>
    public IReadOnlyDictionary<string, CliBinaryStat?>? UnattendedVendorBaselines { get; set; }

    /// <summary>Per-vendor launchable models probed from the installed CLIs. Replaced by reference on
    /// every refresh, never mutated: serializers on other threads may be enumerating the current
    /// instance.</summary>
    public Dictionary<string, VendorModelOption[]>? VendorModels { get; set; }

    /// <summary>Each registered Codex account's launchable models, keyed by registry id. An unregistered
    /// default has no id, so its catalog is only in <see cref="VendorModels"/>. Replaced by reference,
    /// like <see cref="VendorModels"/>.</summary>
    public Dictionary<string, VendorModelOption[]>? VendorModelsByAccount { get; set; }

    /// <summary>Fingerprints of each vendor's catalog files, taken before the startup probe, and of each
    /// account's, taken before its latest probe; the vendor CLI watcher's starting point for an entry
    /// it begins watching. Replaced by reference.</summary>
    public IReadOnlyDictionary<string, CatalogPathStat[]>? VendorCatalogBaselines { get; set; }

    /// <summary>Catalog keys whose startup probe overran its limit and answered afterwards. The vendor CLI
    /// watcher asks for a refresh when one is added, and a refresh probes these vendors though they have no
    /// catalog yet.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> LateCatalogKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Installed vendors that accept a launch-time ACP permission preset, independent of unattended
    /// support, so the server enables the preset selector and refuses a preset elsewhere.
    /// </summary>
    public string[]? AcpPresetVendors { get; set; }

    /// <summary>Vendor tokens this daemon accepts a launch-time permission mode for — Claude when it
    /// is hosted. Sent over <c>DaemonConnect</c> so the server refuses a mode toward a daemon that
    /// would ignore it. <c>null</c> until the host is built.</summary>
    public string[]? PermissionModeVendors { get; set; }

    /// <summary>The home directory this daemon resolved at boot. Every home-derived daemon path
    /// reads it, so a descendant can't be handed a different one than the entry point chose.</summary>
    public UserHome Home {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.Home was never set; pass a {nameof(UserHome)} in from the entry point.");
        set;
    }

    /// <summary>The host's account registry; null leaves the accounts publisher inert.</summary>
    public AccountStore? Accounts { get; set; }

    /// <summary>The config directory each account-bearing vendor uses when no account is chosen.</summary>
    public IReadOnlyDictionary<HarnessId, string> DefaultDirectories { get; set; } = FrozenDictionary<HarnessId, string>.Empty;

    /// <summary>The shared PATH probe (live on macOS), the one the <c>HarnessRegistry</c> is built over. A
    /// configured vendor path is not a harness question, so its resolvers take this.</summary>
    public BinaryProbe Binaries {
        get => field ?? throw new InvalidOperationException($"DaemonConfig.Binaries was never set; pass a {nameof(BinaryProbe)} in from the entry point.");
        set;
    }

    public string WorktreeRoot { get; set; } = "";

    public string ClaudePath { get; set; } = Core.Harness.Claude.ClaudeHarness.CliBinary;
    public string CodexPath  { get; set; } = Core.Harness.Codex.CodexHarness.CliBinary;

    /// <summary>The Codex resolution this daemon routes on and advertises. Swapped whole by the
    /// capability refresh; readers on other threads take the reference once.</summary>
    internal Harness.Codex.CodexTransportResolution CodexResolution {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    } = Harness.Codex.CodexTransportResolution.Unresolved;

    /// <summary>Whether app-server applies when the profile never set the <c>codex-app-server</c>
    /// switch. Set from the platform at boot; off for a config built anywhere else.</summary>
    internal bool CodexAppServerByDefault { get; set; }

    /// <summary>The last resolution a registration carried to the server. A launch that names no
    /// transport follows this one, since it is what the server dispatched against.</summary>
    internal Harness.Codex.CodexTransportResolution AcknowledgedCodexResolution {
        get => Volatile.Read(ref _acknowledgedCodexResolution);
        set => Volatile.Write(ref _acknowledgedCodexResolution, value);
    }

    Harness.Codex.CodexTransportResolution _acknowledgedCodexResolution = Harness.Codex.CodexTransportResolution.Unresolved;

    /// <summary>Records that a registration carried <paramref name="sent"/>. Only a newer generation
    /// moves it, since registrations from different paths can finish out of order.</summary>
    /// <returns>True when the acknowledgement moved.</returns>
    internal bool AcknowledgeCodex(Harness.Codex.CodexTransportResolution sent) {
        while (true) {
            var current = AcknowledgedCodexResolution;
            if (sent.Generation <= current.Generation) return false;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _acknowledgedCodexResolution, sent, current), current)) return true;
        }
    }

    public long ServerAcknowledgedCodexGeneration => AcknowledgedCodexResolution.Generation;

    /// <summary>The newest Codex generation a registration attempt carried and failed to deliver. It
    /// stands for the committed resolution only while that one is still unacknowledged.</summary>
    internal long FailedCodexGeneration => Volatile.Read(ref _failedCodexGeneration);

    long _failedCodexGeneration;

    /// <returns>True when the failed generation moved.</returns>
    internal bool CodexRegistrationFailed(Harness.Codex.CodexTransportResolution sent) {
        while (true) {
            var current = FailedCodexGeneration;
            if (sent.Generation <= current) return false;
            if (Interlocked.CompareExchange(ref _failedCodexGeneration, sent.Generation, current) == current) return true;
        }
    }

    /// <summary>Why <c>experimental.json</c> could not be read on the last resolution pass; null when it was.</summary>
    internal string? ExperimentalSettingsProblem {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>This daemon's profile's experimental switches, read from disk now. Empty when no
    /// profile was resolved.</summary>
    public ExperimentalSettingsRead ReadExperimentalSettings() =>
        ProfileNameOrNull is { } profile ? new ExperimentalSettingsStore(ConfigRoot).Load(profile) : ExperimentalSettingsRead.Empty;

    /// <summary>Seconds an interactive hosted Codex approval (<c>*/requestApproval</c>) waits for the
    /// user before failing closed (deny). Overridable via <c>KCAP_CODEX_APPSERVER_APPROVAL_TIMEOUT_SECONDS</c>.
    /// Consumed as <c>TimeSpan.FromSeconds(Math.Max(1, …))</c>.</summary>
    public int CodexAppServerApprovalTimeoutSeconds { get; set; } = 45;

    /// <summary>The Cursor CLI, spawned as <c>{CursorPath} acp</c>. Env <c>KCAP_CURSOR_PATH</c>.</summary>
    public string CursorPath { get; set; } = Core.Harness.Cursor.CursorHarness.CliBinary;

    /// <summary>
    /// Default Cursor model family. Cursor needs the exact parameterized <c>modelId</c> from
    /// <c>availableModels</c>, so <c>AcpModelResolver.Resolve</c> matches this against it at launch. Env
    /// <c>KCAP_CURSOR_MODEL</c>; a per-launch model wins.
    /// </summary>
    public string CursorModel { get; set; } = "claude-sonnet-4-5";

    /// <summary>The Copilot CLI. Env <c>KCAP_COPILOT_PATH</c>.</summary>
    public string CopilotPath { get; set; } = Core.Harness.Copilot.CopilotHarness.CliBinary;

    /// <summary>The Kiro CLI, spawned as <c>{KiroPath} acp</c>. Env <c>KCAP_KIRO_PATH</c>; only this one
    /// name is probed.</summary>
    public string KiroPath { get; set; } = Core.Harness.Kiro.KiroHarness.CliBinary;

    /// <summary>Default Kiro model, resolved against <c>availableModels</c> and applied with
    /// <c>session/set_model</c>. Null keeps Kiro's own default and reports none. Env <c>KCAP_KIRO_MODEL</c>;
    /// a per-launch model wins.</summary>
    public string? KiroModel { get; set; }

    /// <summary>
    /// Whether this daemon may run Gemini as an unattended reviewer; default true. Set
    /// <c>KCAP_GEMINI_UNATTENDED_REVIEWER=0</c> in the daemon's environment to disable; for a supervised
    /// daemon that is the service unit, which <c>ServiceEnvironment</c> writes. A build below the recorded
    /// floor is still refused (<c>GeminiReviewerCapability</c>).
    /// </summary>
    public bool GeminiUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>Whether this daemon may run Kiro as an unattended reviewer; default true,
    /// <c>KCAP_KIRO_UNATTENDED_REVIEWER=0</c> disables. See <c>KiroReviewerCapability</c>.</summary>
    public bool KiroUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>
    /// One budget for a Kiro reviewer launch, from spawn through the first prompt completing; on expiry the
    /// child is terminated and the launch fails with a coded error. Not per stage, which would multiply it.
    /// An unauthenticated kiro-cli opens a browser prompt and waits forever.
    /// </summary>
    public int KiroReviewerLaunchTimeoutSeconds { get; set; } = 120;

    /// <summary>The Antigravity CLI, spawned per turn. Env <c>KCAP_ANTIGRAVITY_PATH</c>. A name not on PATH
    /// withdraws the vendor silently.</summary>
    public string AntigravityPath { get; set; } = Core.Harness.Antigravity.AntigravityHarness.CliBinary;

    /// <summary>Daemon-wide default model for Antigravity reviewer launches, passed as
    /// <c>--model</c>. Null leaves agy on its own default. An unknown slug makes agy hard-fail,
    /// which is a clean audit signal rather than a silent downgrade.</summary>
    public string? AntigravityModel { get; set; }

    /// <summary>Whether this daemon may run Antigravity unattended reviews; default true,
    /// <c>KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER=0</c> disables. The minimum build is a daemon-owned record,
    /// not config (<c>ReviewerVersionStore</c>).</summary>
    public bool AntigravityUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>Absolute ceiling on the FIRST turn — spawn, NDJSON handshake and auth. An
    /// unauthenticated agy can sit on an interactive OAuth wait, so this is what turns that into a
    /// bounded, coded failure.</summary>
    public int AntigravityReviewerLaunchTimeoutSeconds { get; set; } = 120;

    /// <summary>Ceiling on every SUBSEQUENT turn. Bounding only the launch would leave turn 2+
    /// unbounded, and with ReadOutputAsync parked by design nothing else would ever complete.</summary>
    public int AntigravityReviewerTurnTimeoutSeconds { get; set; } = 600;

    /// <summary>The OpenCode CLI, spawned as <c>{OpenCodePath} acp</c>. Env <c>KCAP_OPENCODE_PATH</c>.</summary>
    public string OpenCodePath { get; set; } = Core.Harness.OpenCode.OpenCodeHarness.CliBinary;

    /// <summary>
    /// Default OpenCode model as <c>provider/model</c> or a display label, resolved against
    /// <c>configOptions</c> and applied with <c>session/set_config_option</c>. Null keeps OpenCode's own
    /// default and reports none. Env <c>KCAP_OPENCODE_MODEL</c>; a per-launch model wins.
    /// </summary>
    public string? OpenCodeModel { get; set; }

    /// <summary>Whether this daemon may run OpenCode as an unattended reviewer; default true,
    /// <c>KCAP_OPENCODE_UNATTENDED_REVIEWER=0</c> disables. See <c>OpenCodeReviewerCapability</c>.</summary>
    public bool OpenCodeUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>Whether this daemon may run Grok Build as an unattended review-flow reviewer. Default
    /// true; <c>KCAP_GROK_UNATTENDED_REVIEWER=0</c> disables. See <c>GrokReviewerCapability</c>.</summary>
    public bool GrokUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>One budget for an OpenCode reviewer launch, from spawn through the first prompt; not per
    /// stage. Covers a CLI that waits on an interactive login instead of failing.</summary>
    public int OpenCodeReviewerLaunchTimeoutSeconds { get; set; } = 120;

    /// <summary>The Pi CLI, spawned as <c>{PiPath} --mode rpc</c>. Env <c>KCAP_PI_PATH</c>.</summary>
    public string PiPath { get; set; } = Core.Harness.Pi.PiHarness.CliBinary;

    /// <summary>Default Pi model, passed as <c>--model</c>. Null keeps Pi's own default and reports none.
    /// Env <c>KCAP_PI_MODEL</c>; a per-launch model other than <c>"default"</c> wins.</summary>
    public string? PiModel { get; set; }

    /// <summary>Whether this daemon may run Pi as an unattended reviewer; default true,
    /// <c>KCAP_PI_UNATTENDED_REVIEWER=0</c> disables. See <c>PiReviewerCapability</c>.</summary>
    public bool PiUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>How long a Pi reviewer round may go without a frame from Pi before it is reaped.</summary>
    public int PiReviewerTurnTimeoutSeconds { get; set; } = 600;

    /// <summary>The Gemini CLI, for hosting and the reviewer, whose build probe reads this binary. Env
    /// <c>KCAP_GEMINI_PATH</c>.</summary>
    public string GeminiPath { get; set; } = Core.Harness.Gemini.GeminiHarness.CliBinary;

    /// <summary>Path or bare command for Mistral Vibe's ACP entry point, spawned with no arguments by
    /// <c>AcpHostedAgentRuntimeFactory</c>. It is <c>vibe-acp</c>, not the <c>vibe</c> TUI. Overridable
    /// via <c>KCAP_MISTRAL_VIBE_PATH</c>.</summary>
    public string MistralVibePath { get; set; } = Core.Harness.MistralVibe.MistralVibeHarness.AcpBinary;

    /// <summary>Path or bare command for Grok Build. Overridable via <c>KCAP_GROK_PATH</c>.</summary>
    public string GrokPath { get; set; } = Core.Harness.Grok.GrokHarness.CliBinary;

    /// <summary>Path or bare command for the Muse Code CLI. Overridable via <c>KCAP_MUSE_PATH</c>.</summary>
    public string MusePath { get; set; } = Core.Harness.Muse.MuseHarness.CliBinary;

    /// <summary>Daemon-wide default model for Muse; null keeps Muse's own default. Overridable via
    /// <c>KCAP_MUSE_MODEL</c>.</summary>
    public string? MuseModel { get; set; }

    /// <summary>Whether THIS daemon may run Muse as an unattended review-flow reviewer. Default TRUE;
    /// <c>KCAP_MUSE_UNATTENDED_REVIEWER=0</c> opts out, as for every gated reviewer.</summary>
    public bool MuseUnattendedReviewerEnabled { get; set; } = true;

    /// <summary>
    /// Opt-in, off-by-default ACP wire/content debug logging (<c>KCAP_ACP_DEBUG_FRAMES</c>). When
    /// <see langword="false"/> (the default), the ACP layers (<c>AcpEventTranslator</c>,
    /// <c>AcpChildProcess</c>, <c>AcpConnection</c>) log shape/length only for the traffic that would
    /// otherwise carry prompt/tool/file content — an unrecognized <c>session/update</c> kind, raw
    /// <c>cursor-agent</c> stderr lines, and full inbound/outbound JSON-RPC frames. When
    /// <see langword="true"/>, those same call sites log full (length-capped) content at Debug for
    /// local troubleshooting — never sent to the server, never written to the transcript. Read from
    /// the env var in <c>DaemonRunner.RunAsync</c>, which also emits a one-time startup Warning when
    /// this is on, since the logged content may include sensitive payloads.
    /// </summary>
    public bool DebugFrames { get; set; }

    /// <summary>
    /// Kill switch for ACP hosted-agent crash reconnect/resume (<c>KCAP_ACP_RECONNECT</c>). Default
    /// ON; set <c>0</c>/<c>false</c> to disable globally. When off — or for a vendor whose
    /// descriptor is not probe-verified reconnect-capable, a launch that is a review flow, or a
    /// session whose handshake didn't advertise <c>loadSession</c> — a child-process death keeps
    /// today's behavior byte-for-byte: the read loop ends and the agent finalizes. Deliberately the
    /// only knob: attempts, backoff, and the per-session resume cap are fixed constants.
    /// </summary>
    public bool AcpReconnectEnabled { get; set; } = true;

    /// <summary>Whether this daemon records Claude sessions itself rather than leave them to a watcher, read from
    /// <c>KCAP_DAEMON_RECORDING</c>: its hosted sessions, and the terminal sessions their hooks hand over, which ride the
    /// same flag. Off by default: a session then records exactly as it would without it.</summary>
    public bool RecordHostedClaude { get; set; }

    /// <summary>
    /// Path to the kcap CLI binary. Used by the daemon to spawn auxiliary
    /// processes (e.g. <c>generate-whats-done</c>) when claude didn't fire its
    /// own session-end hook. Defaults to "kcap" — resolved via PATH, which
    /// works for npm installs that place both <c>kcap</c> and
    /// <c>kcap-daemon</c> in <c>node_modules/.bin</c>.
    /// </summary>
    public string CapacitorPath { get; set; } = "kcap";

    /// <summary>The argv the daemon was launched with, captured for self-respawn (detached restart).</summary>
    public IReadOnlyList<string> OriginalArgs { get; set; } = [];

    /// <summary>
    /// Raw value of <c>KCAP_CONSENT_SEED_DEFAULT</c>, captured off ambient env at boot by
    /// <see cref="DaemonRunner.CaptureBootCarriers"/> and immediately removed from the process
    /// environment so no descendant (PTY-spawned agent, ACP child) can observe it by inheritance.
    /// Unvalidated here — <see cref="DaemonRunner.RunBootChecksAsync"/> classifies/validates this
    /// directive via <c>LaunchConsentStore.BootSeed</c>.
    /// </summary>
    public string? ConsentSeedDirective { get; set; }

    /// <summary>
    /// Raw value of <c>KCAP_EXPECT_SERVER_URL</c>, captured/removed the same way as
    /// <see cref="ConsentSeedDirective"/>. Unvalidated here — <see cref="DaemonRunner.RunBootChecksAsync"/>
    /// checks it against the resolved <see cref="ServerUrl"/>.
    /// </summary>
    public string? ExpectedServerUrl { get; set; }

    /// <summary>
    /// Raw value of <c>KCAP_BOOT_ATTEMPT</c>, captured/removed the same way as
    /// <see cref="ConsentSeedDirective"/>. Per-launch-action: NOT re-injected into a self-respawned
    /// successor (see <c>DetachedRespawnStrategy.SuccessorEnvOverlay</c>) — a self-respawn is not
    /// the app's own action.
    /// </summary>
    public string? BootAttemptId { get; set; }

    public List<string> Validate() {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ServerUrl)) {
            errors.Add("ServerUrl is required");
        } else if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) {
            errors.Add($"ServerUrl must be a valid http/https URL, got: {UnusableUrlDiagnostic.Sanitize(ServerUrl)}");
        }

        if (MaxConcurrentAgents < 0) {
            errors.Add("MaxConcurrentAgents must be 0 (unlimited) or a positive number");
        }

        if (string.IsNullOrWhiteSpace(WorktreeRoot)) {
            errors.Add("WorktreeRoot is required");
        }

        return errors;
    }

    public bool IsRepoAllowed(string repoPath) =>
        AllowedRepoPaths.Length == 0 || AllowedRepoPaths.Any(pattern => RepoPathPattern.Matches(pattern, repoPath));
}
