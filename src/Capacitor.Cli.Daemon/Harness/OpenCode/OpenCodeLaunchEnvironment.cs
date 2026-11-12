using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Harness.OpenCode;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Daemon.Harness.OpenCode;

/// <summary>
/// The environment every daemon-spawned <c>opencode acp</c> child gets, and why.
///
/// <para>OpenCode's launch controls are ENV-shaped, not argv-shaped, which is why this exists as its
/// own unit rather than as an <c>AcpVendorDescriptor.UnattendedTrustArgv</c> entry: there is no argv
/// for any of it. <c>opencode acp</c>'s own option list carries neither <c>--auto</c> nor the config
/// levers — the global flags are not accepted by the subcommand.</para>
/// </summary>
internal static class OpenCodeLaunchEnvironment {
    /// <summary>Disables OpenCode's EXTERNAL plugins — which is to say, kcap's own.</summary>
    internal const string PureVariable = "OPENCODE_PURE";

    /// <summary>A global config layer. One of the three a reviewer has to point at an empty directory.</summary>
    internal const string ConfigDirVariable = "OPENCODE_CONFIG_DIR";

    internal const string HomeVariable = "HOME";

    /// <summary>Where OpenCode looks for <c>.opencode</c>, ahead of the OS home.</summary>
    internal const string TestHomeVariable = "OPENCODE_TEST_HOME";

    /// <summary>The parent of <c>opencode</c>, another global config layer.</summary>
    internal const string ConfigHomeVariable = "XDG_CONFIG_HOME";

    /// <summary>A single config FILE, merged over whatever <see cref="ConfigDirVariable"/> resolves to.</summary>
    internal const string ConfigFileVariable = "OPENCODE_CONFIG";

    /// <summary>Inline config, merged at local scope over both of the above.</summary>
    internal const string ConfigContentVariable = "OPENCODE_CONFIG_CONTENT";

    /// <summary>Suppresses project-scoped config discovery AND the repo instruction-file scan.</summary>
    internal const string ProjectConfigVariable = "OPENCODE_DISABLE_PROJECT_CONFIG";

    /// <summary>The permission table — this vendor's trust vector.</summary>
    internal const string PermissionVariable = "OPENCODE_PERMISSION";

    /// <summary>
    /// Applies the settings that every hosted OpenCode launch needs, interactive included.
    ///
    /// <para><b>Why <c>OPENCODE_PURE=1</c> is not optional.</b> OpenCode is the one vendor where two
    /// kcap capture paths can be live at once: an operator who runs OpenCode normally has kcap's
    /// live-ingest plugin installed at <c>~/.config/opencode/plugins/kcap.ts</c>, and that plugin
    /// loads inside the <c>opencode acp</c> process too. Its <c>session.created</c> handler then
    /// starts a SECOND, top-level capture — a lifecycle POST plus a spawned watcher — for the very
    /// session the ACP mapper is already recording. Measured with a controlled pair
    /// (<c>docs/probes/2026-08-07-opencode-acp/</c>): identical 10s dwell, plugin confirmed
    /// installed, default env created <c>~/.cache/kcap/opencode/&lt;sessionId&gt;.jsonl</c> and
    /// <c>OPENCODE_PURE=1</c> did not.</para>
    ///
    /// <para><b>The precedence this pins:</b> for a daemon-hosted session the ACP mapper is the ONLY
    /// capture path. The plugin keeps its whole job for sessions the user starts themselves — this
    /// suppresses it only inside a child the daemon owns and is already recording.</para>
    ///
    /// <para><b>Accepted cost, stated rather than discovered later:</b> <c>OPENCODE_PURE</c> disables
    /// every external plugin, not just kcap's, so a hosted OpenCode agent does not carry the
    /// operator's other plugins. That diverges from the user's own session, which interactive hosting
    /// otherwise tries to reproduce (compare the Gemini descriptor's note on not pinning
    /// <c>--approval-mode</c>). It is accepted because the alternative is a session that is
    /// double-ingested — and TIMING-DEPENDENTLY so, since whether the plugin wins the race varies
    /// run to run, which is worse to operate than a deterministic duplicate. A narrower fix (the
    /// plugin recognising a daemon-owned launch and standing down) is deliberately NOT layered on
    /// top: with the plugin never loaded that code would be unreachable in production, and an
    /// unreachable guard reads as protection while proving nothing.</para>
    /// </summary>
    internal static void Apply(IDictionary<string, string?> environment) {
        environment[PureVariable] = "1";
    }

    /// <summary>
    /// The settings that additionally make a launch an UNATTENDED REVIEWER, on top of <see cref="Apply"/>. Review
    /// launches only: every one of them would be wrong on an interactive hosted session, which must behave as the
    /// user's own does.
    ///
    /// <para><b>The private config homes are the recursion guard.</b> OpenCode reads the operator's global <c>mcp</c>
    /// servers into every session, the flows server among them. See <see cref="OpenCodeReviewerConfigDir"/>. The user's
    /// <c>model</c> and <c>provider</c> come back through <c>OPENCODE_CONFIG_CONTENT</c>, so the reviewer keeps their
    /// default and any custom provider. Data, state and cache are pinned because they would otherwise follow the
    /// private home, and the data directory holds the sign-in and the sessions a restarted reviewer resumes. Cloud sign-ins
    /// are kept by <see cref="OpenCodeHomeCredentials"/>.</para>
    ///
    /// <para><b><c>OPENCODE_DISABLE_PROJECT_CONFIG=1</c>: the reviewed branch is not trusted input.</b> Without it, a
    /// contributor-authored <c>opencode.json</c> or <c>.opencode/</c> is honoured and the repo's own
    /// <c>AGENTS.md</c>/<c>CLAUDE.md</c> is folded into the reviewer's instructions. It costs the reviewer the repo's
    /// legitimate guidance documents, which is accepted.</para>
    ///
    /// <para><b><c>OPENCODE_PERMISSION</c> is the trust vector.</b> Deny-all plus the read family plus the injected
    /// servers' flattened tool names; see <see cref="OpenCodeReviewerPermissions"/>. It merges over any configuration,
    /// so it holds even against an operator config saying <c>"*": "ask"</c>.</para>
    /// </summary>
    internal static void ApplyReviewer(
            IDictionary<string, string?> environment,
            OpenCodeReviewerDirs dirs,
            JsonObject userConfig,
            IReadOnlyList<AcpMcpServerSpec> injected,
            string userHome) {
        var home = environment.TryGetValue(HomeVariable, out var inherited) && !string.IsNullOrEmpty(inherited) ? inherited : userHome;

        Pin(environment, "XDG_DATA_HOME", home, ".local", "share");
        Pin(environment, "XDG_STATE_HOME", home, ".local", "state");
        Pin(environment, "XDG_CACHE_HOME", home, ".cache");

        environment.Remove(ConfigFileVariable);

        if (userConfig.Count > 0) environment[ConfigContentVariable] = userConfig.ToJsonString();
        else environment.Remove(ConfigContentVariable);

        OpenCodeHomeCredentials.Apply(home, dirs.Home, _ => { });

        environment[HomeVariable]                       = dirs.Home;
        environment[TestHomeVariable]                   = dirs.Home;
        environment[ConfigHomeVariable]                 = dirs.ConfigHome;
        environment[ConfigDirVariable]                  = dirs.ConfigDir;
        environment[ProjectConfigVariable]              = "1";
        environment["OPENCODE_DISABLE_CLAUDE_CODE"]     = "1";
        environment["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "1";
        environment[PermissionVariable]                 = OpenCodeReviewerPermissions.Build(injected);
    }

    /// <summary>The user's model and providers as OpenCode would resolve them under <paramref name="environment"/>. Read
    /// before <see cref="ApplyReviewer"/> replaces the variables that name the layers.</summary>
    internal static JsonObject UserConfig(IDictionary<string, string?> environment, string userHome) {
        var env  = new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        var home = env.GetValueOrDefault(HomeVariable) is { Length: > 0 } inherited ? inherited : userHome;

        return OpenCodeHeadlessConfig.UserModelAndProvider(
            new(new(home), env.GetValueOrDefault(ConfigDirVariable), env.GetValueOrDefault(ConfigHomeVariable), env.GetValueOrDefault("XDG_DATA_HOME")),
            env);
    }

    /// <summary>The injected servers with the user's home: each is a nested <c>kcap</c> that finds its sign-in under it,
    /// and would otherwise inherit the reviewer's private one and fail to report.</summary>
    internal static IReadOnlyList<AcpMcpServerSpec> WithUserHome(IReadOnlyList<AcpMcpServerSpec> servers, string userHome) =>
        [.. servers.Select(s => new AcpMcpServerSpec(s.Name, s.Command, s.Args, [.. s.Env, new(HomeVariable, userHome)]))];

    static void Pin(IDictionary<string, string?> environment, string variable, params string[] underHome) {
        if (environment.TryGetValue(variable, out var value) && !string.IsNullOrEmpty(value)) return;

        environment[variable] = Path.Combine(underHome);
    }
}
