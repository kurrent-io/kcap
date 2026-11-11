using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Daemon.Harness.Kiro;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Harness.Antigravity;

/// <summary>
/// Keep the capture plugin directory absent: its watcher would duplicate ingestion and can keep
/// turn stdout open after the child exits. MCP config contains only injected servers, never a merge
/// with the operator's global servers; settings explicitly permit the reviewer's result-channel tools.
/// Conversation state and transcripts contain caller-sensitive data, so dispose the isolated HOME.
/// Use a per-daemon root: cleanup by boot epoch in a shared root would delete a peer's live home.
/// </summary>
internal static class AntigravityReviewerHome {
    const string Prefix = "kcap-antigravity-reviewer-";

    /// <summary>This daemon's own reviewer-home root. Never shared with a peer daemon.</summary>
    internal static string RootFor(string stateDir) => Path.Combine(stateDir, "antigravity-reviewers");

    /// <summary>One derivation of a home's directory name. Create and delete both go through it, so a
    /// failed launch's cleanup cannot target a path the launch never made.</summary>
    internal static string NameFor(string daemonEpoch, string launchId) =>
        $"{Prefix}{Sanitize(daemonEpoch)}-{Sanitize(launchId)}";

    /// <summary>
    /// Creates an owner-only home carrying a fresh <c>mcp_config.json</c> for
    /// <paramref name="injected"/>, and — for an unattended review — the <c>settings.json</c> that
    /// grants those servers' tools. Never seeds the kcap plugin directory — that absence is what job
    /// 1 above depends on, so it is verified after writing rather than assumed.
    /// </summary>
    /// <param name="grantInjectedMcpTools">Whether this launch's injected MCP tools need
    /// <c>permissions.allow</c> rules — true for an unattended review, false for a hosted agent.
    ///
    /// <para><b>A parameter rather than "grant whatever was injected".</b> A hosted launch already
    /// passes <c>--dangerously-skip-permissions</c>, so a rule for it would be dead config; and its
    /// injected list is <c>ctx.McpServers</c>, whose contents are a caller's rather than a review
    /// definition's — deriving rules from it would put an unclassifiable server through
    /// <see cref="AntigravityReviewerPermissions"/>'s fail-closed throw and refuse a launch that was
    /// never asking for a grant. Deliberately not defaulted: a caller that omits it would get a
    /// reviewer that starts, runs, and is auto-denied on the one call the round waits for — the exact
    /// defect this parameter exists to fix.</para></param>
    internal static string Create(string stateDir, string daemonEpoch, string launchId,
                                  IReadOnlyList<AcpMcpServerSpec> injected, bool grantInjectedMcpTools,
                                  ILogger? log = null) {
        var root = RootFor(stateDir);
        CreateOwnerOnly(root);

        var home = Path.Combine(root, NameFor(daemonEpoch, launchId));

        // A repeated launch under the same epoch+agent id must not inherit the previous one's
        // conversation state (brain/, conversations/*.db) or a stale mcp_config.json:
        // CreateDirectory silently succeeds on an existing directory, so "fresh" would be a hope
        // rather than a property. Remove first, then create.
        if (Path.Exists(home)) Delete(home, stateDir, log ?? NullLogger.Instance);

        CreateOwnerOnly(home);

        // Delete is best-effort by design (an undeletable home must not fail a round), so a partial
        // failure could leave the previous launch's brain/conversations content in place, and
        // CreateOwnerOnly would happily accept the surviving directory — it does not check
        // emptiness. A silently inherited conversation is the worst outcome this class can produce
        // (the reviewer would resume someone else's review with no signal), so freshness is
        // verified here, before anything is written, rather than assumed from "Delete ran".
        if (Directory.EnumerateFileSystemEntries(home).Any())
            throw new InvalidOperationException(
                $"antigravity_reviewer_home_not_empty: '{home}' still holds content from a previous "
              + "launch that could not be removed. Refusing rather than handing a reviewer another "
              + "review's conversation state.");

        var paths = LayoutFor(home);

        // Everything from the first WRITE onward is all-or-nothing. The caller binds this home's
        // disposal to the runtime it creates AFTER Create returns, so a throw from in here leaves a
        // home with no disposal path armed at all — and the first thing written, mcp_config.json,
        // carries the launch's live loopback capability URL. The daemon-epoch sweep would eventually
        // reclaim it, but that is the crash backstop, not a licence to leak on an ordinary refusal.
        // WriteSettings' fail-closed throw for an unclassifiable server makes this reachable by
        // configuration rather than only by IO failure.
        //
        // Scoped to start here deliberately: the emptiness refusal above keeps its existing
        // behaviour of leaving the previous launch's undeletable content alone, and nothing
        // capability-bearing exists before this point.
        try {
            WriteMcpConfig(home, paths, injected);

            // Injecting a server is only half of a usable reviewer — see WriteSettings.
            if (grantInjectedMcpTools) WriteSettings(home, paths, injected);

            // The kcap plugin dir is what lets agy's OWN capture hooks fire (job 1) — its absence is
            // the whole mechanism, so it is checked rather than trusted to follow from "we never wrote
            // it". A future change that seeds a fuller home must trip this, not silently double-capture.
            if (Directory.Exists(paths.PluginDir))
                throw new InvalidOperationException(
                    $"antigravity_reviewer_home_not_isolated: '{home}' carries a kcap plugin directory, "
                  + "which would let this reviewer's own capture hooks fire against its conversation.");
        } catch {
            // Best-effort, and never allowed to replace the real reason: a cleanup fault here would
            // otherwise mask the fail-closed refusal the caller needs to see.
            Delete(home, stateDir, log ?? NullLogger.Instance);
            throw;
        }

        return home;
    }

    /// <summary>This home's Antigravity layout, derived from the home directory alone. The null
    /// override is load-bearing: <c>GEMINI_CLI_HOME</c> REPLACES the Gemini root, so honouring it
    /// here would resolve every file below outside the isolated home.</summary>
    static AntigravityPaths LayoutFor(string home) => new AntigravityPaths(new(home), null);

    /// <summary>Writes <c>{home}/.gemini/config/mcp_config.json</c> — the plain, trust-less
    /// <c>mcpServers</c> shape (<c>McpConfigShape.Standard</c>) — holding only <paramref name="injected"/>.
    /// A fresh write into a fresh directory, so unlike <c>JsonMcpConfigWriter</c> (which merges into
    /// and preserves an operator's live config) there is nothing to read-modify-write or mark as
    /// kcap-owned: the whole file is this launch's content, one shot, atomic-by-single-write.
    /// Built via <c>(JsonNode?)</c> string casts, not <c>JsonValue.Create</c> / collection expressions
    /// — the latter lower to a generic <c>Add&lt;T&gt;</c> that trips NativeAOT (IL3050), the same
    /// rule <c>ClaudeLauncher.BuildReviewFlowMcpConfig</c> and <c>ReviewLaunchBuilder</c> follow.</summary>
    static void WriteMcpConfig(string home, AntigravityPaths paths, IReadOnlyList<AcpMcpServerSpec> injected) {
        var pathFull = ResolveInsideHome(home, paths.McpConfigJson, "mcp_config.json");

        Directory.CreateDirectory(Path.GetDirectoryName(pathFull)!);

        var mcpServers = new JsonObject();

        foreach (var server in injected) {
            var args = new JsonArray();
            foreach (var a in server.Args) args.Add((JsonNode?)a);

            var env = new JsonObject();
            foreach (var e in server.Env) env[e.Name] = (JsonNode?)e.Value;

            mcpServers[server.Name] = new JsonObject {
                ["command"] = (JsonNode?)server.Command,
                ["args"]    = args,
                ["env"]     = env
            };
        }

        var root = new JsonObject { ["mcpServers"] = mcpServers };
        File.WriteAllText(pathFull, root.ToJsonString());
    }

    /// <summary>
    /// Writes <c>{home}/.gemini/antigravity-cli/settings.json</c> — the <c>agy</c> CLI's OWN settings
    /// file, a different file from the <c>mcp_config.json</c> above — carrying nothing but the
    /// <c>permissions.allow</c> rules for <paramref name="injected"/>.
    ///
    /// <para><b>Injecting a server is only half of a usable reviewer.</b> <c>agy -p</c> has no human to
    /// answer a tool confirmation, so it auto-denies every one it raises — and the reviewer's result
    /// channel IS an MCP tool, which made the one call each round depends on the one call print mode
    /// refused. Measured: the conversation stopped at <c>PLANNER_RESPONSE</c> with no <c>TOOL_CALL</c>,
    /// agy logged <c>user denied permission for mcp</c>, and every round hung to the flow's timeout. See
    /// <see cref="AntigravityReviewerPermissions"/> for the rule form and why not
    /// <c>--dangerously-skip-permissions</c>.</para>
    ///
    /// <para>Same fresh-write-into-a-fresh-directory shape as <see cref="WriteMcpConfig"/>: the whole
    /// file is this launch's content, so there is no operator config to merge with — which is also what
    /// makes the grant exactly this launch's and not the operator's own rules plus it.</para>
    /// </summary>
    static void WriteSettings(string home, AntigravityPaths paths, IReadOnlyList<AcpMcpServerSpec> injected) {
        var pathFull = ResolveInsideHome(home, paths.CliSettingsJson, "settings.json");

        Directory.CreateDirectory(Path.GetDirectoryName(pathFull)!);

        var allow = new JsonArray();

        // Built BEFORE anything is written: an unclassifiable server throws, and a home left carrying a
        // half-written grant would be worse than one carrying none.
        foreach (var rule in AntigravityReviewerPermissions.AllowRulesFor(injected)) allow.Add((JsonNode?)rule);

        var root = new JsonObject { ["permissions"] = new JsonObject { ["allow"] = allow } };
        File.WriteAllText(pathFull, root.ToJsonString());
    }

    /// <summary>
    /// The full path of a file this class means to write INSIDE <paramref name="home"/>, or a throw.
    /// Isolation is this class's whole purpose, so containment is verified for every file the home
    /// writes rather than assumed from <see cref="LayoutFor"/> — a derivation that honoured a vendor
    /// override would write the reviewer's result channel, or the grants that admit it, into the
    /// operator's own Gemini tree.
    /// </summary>
    internal static string ResolveInsideHome(string home, string path, string what) {
        var homeFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        var pathFull = Path.GetFullPath(path);

        if (!pathFull.StartsWith(homeFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"antigravity_reviewer_home_escaped_root: {what} resolved to '{pathFull}', "
              + $"outside the isolated home '{homeFull}'. Refusing to write a reviewer's result "
              + "channel, or the grants that admit it, outside its isolated home.");

        return pathFull;
    }

    /// <summary>
    /// Deletes every home in THIS daemon's root whose epoch is not the current one — the crash and
    /// SIGKILL recovery. Safe by construction because the root is not shared.
    /// </summary>
    /// <para><b>Known ordering, accepted:</b> this runs at daemon start, before the orphan-agent
    /// reaper — same accepted residual as <see cref="KiroReviewerHome.SweepStale"/>.</para>
    internal static void SweepStale(string stateDir, string currentEpoch, ILogger? log = null) {
        log ??= NullLogger.Instance;
        var root = RootFor(stateDir);
        if (!Directory.Exists(root)) return;

        var live = $"{Prefix}{Sanitize(currentEpoch)}-";

        IReadOnlyList<string> candidates;

        try {
            candidates = [.. Directory.EnumerateDirectories(root)];
        } catch (Exception ex) {
            log.LogWarning(ex, "Could not enumerate the Antigravity reviewer home root {Root}; skipping the sweep", root);
            return;
        }

        foreach (var dir in candidates) {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (name.StartsWith(live,   StringComparison.Ordinal)) continue;

            Delete(dir, stateDir, log);
        }
    }

    /// <summary>
    /// Removes a reviewer home. Never follows a link out of the tree, and refuses a path that does
    /// not resolve inside this daemon's root — the same two recursive-delete hazards
    /// <see cref="KiroReviewerHome.Delete"/> guards against, for the same reason: the content is
    /// review context (transcript, diff, findings), not disposable scratch.
    /// </summary>
    internal static void Delete(string homePath, string stateDir, ILogger? log = null) {
        log ??= NullLogger.Instance;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootFor(stateDir)));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(homePath));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            log.LogWarning("Antigravity reviewer home {Path} resolves outside {Root}; refusing to delete", full, root);
            return;
        }

        try {
            // The containment check above is LEXICAL, so a link planted AT the home path resolves
            // inside the root and would then be enumerated — following it into the target and
            // deleting the target's contents. DeleteTreeNoFollow protects nested entries but takes
            // the top-level path on trust, so the top level is checked here.
            var attributes = new FileInfo(full).Attributes;

            if (attributes.HasFlag(FileAttributes.ReparsePoint)) {
                if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(full);
                else                                             File.Delete(full);

                return;
            }

            DeleteTreeNoFollow(full);
        } catch (Exception ex) {
            // Log and continue: an undeletable home must not fail a round or block daemon startup.
            // It IS undisposed review context accumulating on disk, so it is never silent.
            log.LogWarning(ex, "Failed to delete Antigravity reviewer home {Path}", full);
        }
    }

    /// <summary>
    /// Recursive delete that unlinks a directory symlink rather than descending through it. Kind is
    /// read from the attributes, which do NOT follow — <c>Directory.Exists</c> does, and would report
    /// false for a dangling directory link, falling through to a <c>File.Delete</c> that a Windows
    /// reparse point rejects.
    /// </summary>
    static void DeleteTreeNoFollow(string path) {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) {
            var attributes  = new FileInfo(entry).Attributes;
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isLink      = attributes.HasFlag(FileAttributes.ReparsePoint);

            // Windows refuses to delete a read-only entry, and whatever agy or a library caches into
            // the home can leave one; an undeleted home is review context left on disk.
            if (!isLink && attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);

            if (isDirectory && isLink) Directory.Delete(entry);   // the link; its target is untouched
            else if (isDirectory)      DeleteTreeNoFollow(entry);
            else                       File.Delete(entry);
        }

        Directory.Delete(path);
    }

    /// <summary>
    /// Creates a directory owner-only from its first instant (<c>0700</c>, or a protected DACL for the
    /// user and SYSTEM on Windows) and reads it back. Never best-effort, unlike the daemon's own state
    /// directory: the restriction is the protection for the conversation state written into it, so a
    /// host that cannot achieve or confirm it refuses the launch.
    /// </summary>
    static void CreateOwnerOnly(string path) {
        try {
            OwnerOnlyDirectory.CreateVerified(path);
        } catch (Exception ex) {
            throw new InvalidOperationException(
                $"antigravity_reviewer_home_not_owner_only: '{path}' could not be made owner-only "
              + $"({ex.Message}). The agent's conversation state, and so any review context, would be "
              + "readable by other users on this host.", ex);
        }
    }

    static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
