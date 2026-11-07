using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Workspace;

/// <summary>Takes <see cref="WorkspaceSnapshot"/>s of a working tree within <see cref="WorkspaceBounds"/>.</summary>
public sealed class WorkspaceSnapshotter(WorkspaceGit git, TimeProvider time, WorkspaceBounds? bounds = null) {
    public WorkspaceBounds Bounds { get; } = bounds ?? WorkspaceBounds.Default;

    public WorkspaceGit Git => git;

    /// <summary>The working tree containing <paramref name="dir"/>, or null outside any repository.</summary>
    public async Task<WorkspaceRepo?> ResolveAsync(string dir, TimeSpan timeout) {
        if (!Directory.Exists(dir)) return null;

        var result = await git.RunAsync(dir, timeout, "rev-parse", "--path-format=absolute", "--show-toplevel", "--git-common-dir");
        if (!result.Ok) return null;

        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !Path.IsPathRooted(lines[0])) return null;

        return new WorkspaceRepo(Normalize(lines[0]), Normalize(lines[1]));
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Null when git status could not be read in time: a snapshot without it would make every
    /// dirty path look changed against the other end of the span.</summary>
    public async Task<WorkspaceSnapshot?> TakeAsync(string toplevel, TimeSpan budget) {
        var started   = time.GetTimestamp();
        TimeSpan Left() => budget - time.GetElapsedTime(started);
        var truncated = false;

        var headRun = await git.RunAsync(toplevel, Left(), "rev-parse", "--verify", "-q", "HEAD");
        var head    = headRun.Ok ? headRun.Stdout.Trim() : WorkspaceSnapshot.Unborn;
        // Only a clean "no such ref" is an unborn branch. Taking a failed run for one would read every
        // commit the reflog has as gained during the span.
        var headKnown = headRun.Ok || headRun is { TimedOut: false, ExitCode: 1, Stderr.Length: 0 };

        var headReflog = !headKnown ? -1
                       : head == WorkspaceSnapshot.Unborn ? 0
                       : await ReflogLengthAsync(toplevel, "HEAD", Left());
        var stashReflog = await ReflogLengthAsync(toplevel, "refs/stash", Left());

        var status = await git.RunAsync(toplevel, Left(), "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (!status.Ok) return null;
        if (status.Cut) truncated = true;

        var entries = new Dictionary<string, WorkspaceEntry>(StringComparer.Ordinal);
        foreach (var record in GitPorcelain.ParseZ(status.Stdout)) {
            if (entries.Count >= Bounds.StatusEntries) {
                truncated = true;
                break;
            }
            if (!IsInside(record.Path)) continue;
            entries[record.Path] = new WorkspaceEntry(record.Code, record.OriginalPath, Hash: null);
        }

        var hasher = new Hasher(Bounds, Left);
        foreach (var path in entries.Keys.ToList()) {
            var entry = entries[path];
            if (IsAbsentFromTree(entry.Code)) continue;

            var full = Path.Combine(toplevel, path);
            var hash = IsSubmodule(full) ? await SubmoduleStampAsync(full, hasher) : hasher.Hash(full);
            entries[path] = entry with { Hash = hash };
        }

        return new WorkspaceSnapshot {
            Head        = head,
            HeadReflog  = headReflog,
            StashReflog = stashReflog,
            Entries     = entries,
            Truncated   = truncated || hasher.Truncated || headReflog < 0 || stashReflog < 0,
        };
    }

    /// <summary>A path git reports relative to the toplevel and never above it.</summary>
    public static bool IsInside(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path)
     && !path.Split('/', '\\').Any(segment => segment is ".." or "." or "");

    static bool IsAbsentFromTree(string code) => code[1] == 'D' || (code[0] == 'D' && code[1] == ' ');

    static bool IsSubmodule(string full) => Directory.Exists(full) && Path.Exists(Path.Combine(full, ".git"));

    async Task<int> ReflogLengthAsync(string toplevel, string reference, TimeSpan timeout) {
        var run = await git.RunAsync(toplevel, timeout, "rev-list", "--walk-reflogs", "--count", reference, "--");
        if (run.Ok && int.TryParse(run.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)) return count;
        if (run.TimedOut) return -1;

        // A reference with no reflog yet has length 0, and a stash that was never made is the common case.
        var exists = await git.RunAsync(toplevel, timeout, "rev-parse", "--verify", "-q", reference);

        return exists is { Ok: false, TimedOut: false } ? 0 : -1;
    }

    /// <summary>Changes whenever the submodule's HEAD, its status, or the content of any of its dirty files
    /// changes, so a second edit to an already-dirty file inside it still shows.</summary>
    async Task<string?> SubmoduleStampAsync(string dir, Hasher hasher) {
        var head   = await git.RunAsync(dir, hasher.Left(), "rev-parse", "--verify", "-q", "HEAD");
        var status = await git.RunAsync(dir, hasher.Left(), "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (!status.Ok || status.Cut || head.TimedOut) {
            hasher.Truncated = true;
            return null;
        }

        var stamp = new StringBuilder().Append(head.Stdout.Trim()).Append('\0').Append(status.Stdout).Append('\0');
        foreach (var record in GitPorcelain.ParseZ(status.Stdout)) {
            if (IsAbsentFromTree(record.Code) || !IsInside(record.Path)) continue;
            var full = Path.Combine(dir, record.Path);
            stamp.Append(record.Path).Append(':').Append(IsSubmodule(full) ? "submodule" : hasher.Hash(full)).Append('\n');
        }

        return "submodule:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString())));
    }

    sealed class Hasher(WorkspaceBounds bounds, Func<TimeSpan> left) {
        int  _paths;
        long _bytes;
        bool _stopped;

        public bool Truncated { get; set; }

        public TimeSpan Left() => left();

        public string? Hash(string full) {
            if (_stopped || _paths >= bounds.HashedPaths || left() <= TimeSpan.Zero) {
                _stopped  = true;
                Truncated = true;
                return null;
            }

            try {
                var info = new FileInfo(full);
                if (info.LinkTarget is { } target) {
                    _paths++;
                    return "link:" + target;
                }
                if (!info.Exists) return null;

                if (info.Length > bounds.FileBytes) {
                    _paths++;
                    Truncated = true;
                    return string.Create(CultureInfo.InvariantCulture, $"stat:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
                }
                if (_bytes + info.Length > bounds.HashedBytes) {
                    _stopped  = true;
                    Truncated = true;
                    return null;
                }

                _paths++;
                _bytes += info.Length;
                using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return null;
            }
        }
    }
}
