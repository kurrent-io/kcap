using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Recording.PrDetection;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The one place <c>kcap import</c> decides whether a session is inside the profile's capture
/// scope. Sources ask before probing, and the orchestrator scopes local failures too.
/// Repository detection is shared and bounded across every source.
/// </summary>
sealed class CaptureScope {
    readonly UserHome                               _home;
    readonly string[]?                              _allowedPaths;
    readonly string[]?                              _excludedPaths;
    readonly string[]?                              _allowedRepos;
    readonly string[]?                              _excludedRepos;
    readonly Func<string, Task<RepositoryPayload?>> _repoDetector;
    readonly ConcurrentDictionary<string, string?> _repoCache = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _repoDetections = new(StringComparer.Ordinal);

    public CaptureScope(
            GitProviderRouter router, ConfigRoot config, UserHome home,
            string[]? allowedPaths, string[]? excludedPaths,
            string[]? allowedRepos, string[]? excludedRepos, TimeProvider time,
            Func<string, Task<RepositoryPayload?>>? repoDetector = null) {
        _home          = home;
        _allowedPaths  = allowedPaths;
        _excludedPaths = excludedPaths;
        _allowedRepos  = allowedRepos;
        _excludedRepos = excludedRepos;
        _repoDetector  = repoDetector
                      ?? (cwd => RepositoryDetection.DetectRepositoryAsync(router, config, cwd, time, detectPullRequest: false));
    }

    /// <summary>Whether any list is set. Nothing to do when the profile scopes nothing.</summary>
    public bool Configured => NeedsRepo || NeedsPath;

    bool NeedsRepo     => _allowedRepos is { Length: > 0 } || ExcludesRepos;
    bool ExcludesRepos => _excludedRepos is { Length: > 0 };
    bool NeedsPath     => _allowedPaths is { Length: > 0 } || _excludedPaths is { Length: > 0 };

    /// <summary>
    /// Stamps the scope verdict onto the sessions this run could act on, and onto those whose probe
    /// failed. Already-loaded counts: it still reaches <c>ImportSessionAsync</c> to re-assert its
    /// lifecycle hooks, so one outside the scope has to carry the verdict that keeps it out. A probe
    /// failure says nothing about scope, and leaving an excluded one unstamped would count it as a
    /// failure to retry while the server is down and as excluded once it is back. A session that is
    /// too short or internal reaches nothing either way and keeps its own status.
    ///
    /// <para><paramref name="resolvedCwds"/> holds the remapped, worktree-stripped cwd that
    /// <c>--repo</c> / <c>--org</c> matched each session on. The raw cwd of a deleted worktree
    /// detects no repo, so <c>allowed_repos</c> judges the resolved one. Every exclusion also
    /// judges the raw cwd, because the transcript carries that path to the server and a remap
    /// must not move a session out from under an exclusion.</para>
    /// </summary>
    public async Task<List<ImportCommand.SessionClassification>> ApplyAsync(
            IReadOnlyList<ImportCommand.SessionClassification> classifications,
            IReadOnlyDictionary<string, string> resolvedCwds) {
        if (NeedsRepo) await WarmRepoCacheAsync(classifications, resolvedCwds);

        return [.. classifications.Select(c => Scoped(c) ? Stamp(c, resolvedCwds) : c)];
    }

    public async Task<ImportCommand.SessionClassification?> ExcludeBeforeProbeAsync(
            ImportCommand.SessionClassification candidate, IReadOnlyDictionary<string, string> resolvedCwds) {
        if (!Configured) return null;

        var scoped = (await ApplyAsync([candidate], resolvedCwds))[0];
        return scoped.ExcludedRepoKeys.Count > 0 || scoped.ExcludedPathKey is not null || scoped.OutsideAllowlist
            ? scoped with { Status = ImportCommand.ClassificationStatus.Excluded }
            : null;
    }

    /// <summary>
    /// Whether the profile keeps a discovered session out of any import, judged as <see cref="Stamp"/> judges it:
    /// paths on the raw cwd alone, so a remap into an ignored folder does not keep a session out, and
    /// <c>excluded_repos</c> on both the resolved repository and <paramref name="rawRepo"/>, the raw cwd's own.
    /// </summary>
    internal static bool ExcludesDiscovered(
            Profile profile, UserHome home, string? rawCwd, (string Owner, string Name)? repo, string? rawRepo = null) {
        var repoKey = repo is { } r ? $"{r.Owner}/{r.Name}" : null;

        return ExcludesRecorded(profile, home, rawCwd, rawRepo)
            || RepoScope.IsOutOfScope(repoKey, profile.AllowedRepos, profile.ExcludedRepos);
    }

    /// <summary>The part of <see cref="ExcludesDiscovered"/> judged on the raw cwd alone, which no remap can lift.</summary>
    internal static bool ExcludesRecorded(Profile profile, UserHome home, string? rawCwd, string? rawRepo) =>
        PathExclusion.IsExcluded(rawCwd, profile.ExcludedPaths, home)
     || PathExclusion.IsOutsideAllowlist(rawCwd, profile.AllowedPaths, home)
     || rawRepo is not null && profile.ExcludedRepos is { Length: > 0 } && RepoScope.Names(profile.ExcludedRepos, rawRepo);

    /// <summary>
    /// The statuses that can still reach the server this run: <c>BuildImportChains</c> takes New and
    /// Partial, and the routed path additionally re-asserts AlreadyLoaded.
    /// </summary>
    internal static bool Actionable(ImportCommand.SessionClassification c) =>
        c.Status is ImportCommand.ClassificationStatus.New
                 or ImportCommand.ClassificationStatus.Partial
                 or ImportCommand.ClassificationStatus.AlreadyLoaded;

    /// <summary>The sessions <see cref="ApplyAsync"/> stamps.</summary>
    internal static bool Scoped(ImportCommand.SessionClassification c) =>
        Actionable(c) || c.Status is ImportCommand.ClassificationStatus.ProbeError;

    /// <summary>
    /// Resolves every distinct cwd up front, bounded, so a few hundred workspaces are not a serial
    /// wall of <c>git</c> invocations. Each
    /// miss can spawn several processes under a 5s cap, and one unreachable path costs that alone.
    /// </summary>
    async Task WarmRepoCacheAsync(
            IReadOnlyList<ImportCommand.SessionClassification> classifications,
            IReadOnlyDictionary<string, string> resolvedCwds) {
        var cwds = classifications.Where(Scoped)
            .SelectMany(c => ExcludesRepos
                            ? new[] { ImportCommand.ResolveCwd(c, resolvedCwds), CwdOf(c) }
                            : new[] { ImportCommand.ResolveCwd(c, resolvedCwds) })
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(cwd => !_repoCache.ContainsKey(cwd))
            .ToList();

        await Parallel.ForEachAsync(
            cwds,
            new ParallelOptions { MaxDegreeOfParallelism = classifications.Count == 1 ? 1 : 8 },
            async (cwd, _) => _repoCache[cwd] = await _repoDetections
                .GetOrAdd(cwd, path => new Lazy<Task<string?>>(() => DetectAsync(path))).Value);
    }

    ImportCommand.SessionClassification Stamp(
            ImportCommand.SessionClassification c, IReadOnlyDictionary<string, string> resolvedCwds) {
        var cwd     = CwdOf(c);
        // A harness's own record is where the import places the session, so it is judged in place of the folder.
        var recorded = ImportCommand.ParseRecordedRepository(c.SourceMeta?.GetValueOrDefault(ImportCommand.RecordedRepositoryKey) as string) is { } r
            ? $"{r.Owner}/{r.Name}"
            : null;
        var repoKey = recorded ?? RepoKeyAt(ImportCommand.ResolveCwd(c, resolvedCwds));

        string[] excludedRepoKeys = ExcludesRepos
            ? [.. new[] { ExcludedRepo(repoKey), ExcludedRepo(RepoKeyAt(ImportCommand.ResolveCwd(c, resolvedCwds))), ExcludedRepo(RepoKeyAt(cwd)) }
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];

        string? excludedPathKey = null;

        if (cwd is not null && _excludedPaths is { Length: > 0 }) {
            foreach (var entry in _excludedPaths) {
                if (!PathExclusion.IsExcluded(cwd, [entry], _home)) continue;

                excludedPathKey = PathExclusion.Normalize(entry, _home);
                break;
            }
        }

        // A cwd or repo that could not be resolved is outside a configured allow list — the same
        // call the live hooks make, and the reason this runs even when cwd is null.
        var outsideAllowlist = PathExclusion.IsOutsideAllowlist(cwd, _allowedPaths, _home)
                            || RepoExclusion.IsOutsideAllowlist(repoKey, _allowedRepos);

        return c with {
            ExcludedRepoKeys = excludedRepoKeys,
            ExcludedPathKey  = excludedPathKey,
            OutsideAllowlist = outsideAllowlist,
        };
    }

    /// <summary>
    /// The best cwd known for a session: the one its transcript reported, else the one decoded from
    /// the project directory name. Claude and Codex resolve it from their transcript before probing.
    /// </summary>
    static string? CwdOf(ImportCommand.SessionClassification c) =>
        c.Meta.Cwd ?? (string.IsNullOrEmpty(c.EncodedCwd) ? null : SessionImporter.DecodeCwdFromDirName(c.EncodedCwd));

    string? RepoKeyAt(string? cwd) =>
        NeedsRepo && cwd is not null && _repoCache.TryGetValue(cwd, out var key) ? key : null;

    string? ExcludedRepo(string? repoKey) =>
        repoKey is not null && _excludedRepos!.Contains(repoKey, StringComparer.OrdinalIgnoreCase) ? repoKey : null;

    async Task<string?> DetectAsync(string cwd) {
        try {
            var repo = await _repoDetector(cwd);

            if (repo is { Owner: { } owner, RepoName: { } repoName }) return $"{owner}/{repoName}";
        } catch {
            // Unresolvable, which the allow list reads as "not admitted" and the deny list ignores.
        }

        return null;
    }
}
