using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

public sealed class GitBranchReader(IProcessRunner runner, TimeProvider time) : IBranchReader {
    static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);
    const int OutputLimit = 4 * 1024 * 1024;

    // Nothing may wait on a person: the app has no terminal, and a credential helper or ssh
    // that prompts would hold the lookup until its timeout.
    static readonly Dictionary<string, string> NoPrompts = new(StringComparer.Ordinal) {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["SSH_ASKPASS_REQUIRE"] = "never",
    };

    readonly Lock _lock = new();
    readonly Dictionary<string, (DateTimeOffset At, BranchListing Listing)> _cache = new(StringComparer.Ordinal);

    public Task<BranchListing?> ListLocalAsync(string repoPath, CancellationToken ct) =>
        CachedAsync($"local\n{repoPath}",
            ["-C", repoPath, "for-each-ref", "--format=%(refname)%09%(symref)", "refs/heads", "refs/remotes/origin"],
            LocalTimeout, ParseRefs, ct);

    public Task<BranchListing?> ListRemoteAsync(string url, CancellationToken ct) =>
        url.Length == 0 || url.StartsWith('-')
            ? Task.FromResult<BranchListing?>(null)
            : CachedAsync($"remote\n{url}",
                ["-c", "protocol.ext.allow=never", "ls-remote", "--symref", url, "HEAD", "refs/heads/*"],
                RemoteTimeout, ParseLsRemote, ct);

    public string? OriginUrlOf(string repoPath) {
        try { return GitRemoteReader.ReadOriginUrl(GitRepository.ResolveMainRepoRoot(repoPath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    async Task<BranchListing?> CachedAsync(
            string key, string[] args, TimeSpan timeout, Func<string, BranchListing> parse, CancellationToken ct) {
        lock (_lock) {
            if (_cache.TryGetValue(key, out var hit) && time.GetUtcNow() - hit.At < Fresh) return hit.Listing;
        }

        ProcessResult result;
        try {
            result = await runner.RunBoundedAsync("git", args,
                new RunOptions(NoPrompts, timeout, CancelMode.KillTree), OutputLimit, ct).ConfigureAwait(false);
        } catch (Exception e) when (e is not OperationCanceledException) {
            return null;
        }
        if (result.TimedOut || result.Oversized || result.ExitCode != 0) return null;

        var listing = parse(result.Stdout);
        lock (_lock) _cache[key] = (time.GetUtcNow(), listing);
        return listing;
    }

    /// <c>for-each-ref</c> lines of "refname TAB symref": local branches and origin's tracking
    /// branches as one list, with origin/HEAD naming the default.
    public static BranchListing ParseRefs(string output) {
        const string Heads = "refs/heads/", Origin = "refs/remotes/origin/";
        string? defaultBranch = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(output)) {
            var tab = line.IndexOf('\t');
            var name = tab < 0 ? line : line[..tab];
            var symref = tab < 0 ? "" : line[(tab + 1)..];
            if (name == Origin + "HEAD") {
                if (symref.StartsWith(Origin, StringComparison.Ordinal)) defaultBranch = symref[Origin.Length..];
            } else if (name.StartsWith(Heads, StringComparison.Ordinal)) {
                names.Add(name[Heads.Length..]);
            } else if (name.StartsWith(Origin, StringComparison.Ordinal)) {
                names.Add(name[Origin.Length..]);
            }
        }
        return Listing(defaultBranch, names);
    }

    /// <c>ls-remote --symref</c> output: "ref: refs/heads/X TAB HEAD" names the default, and
    /// "sha TAB refs/heads/X" each branch.
    public static BranchListing ParseLsRemote(string output) {
        const string Heads = "refs/heads/", SymrefPrefix = "ref: ";
        string? defaultBranch = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(output)) {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var left = line[..tab];
            var right = line[(tab + 1)..];
            if (left.StartsWith(SymrefPrefix, StringComparison.Ordinal)) {
                var target = left[SymrefPrefix.Length..];
                if (right == "HEAD" && target.StartsWith(Heads, StringComparison.Ordinal)) defaultBranch = target[Heads.Length..];
            } else if (right.StartsWith(Heads, StringComparison.Ordinal)) {
                names.Add(right[Heads.Length..]);
            }
        }
        return Listing(defaultBranch, names);
    }

    static BranchListing Listing(string? defaultBranch, HashSet<string> names) {
        if (defaultBranch is not null) names.Add(defaultBranch);
        return new(defaultBranch, names.Order(StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal).ToArray());
    }

    static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
