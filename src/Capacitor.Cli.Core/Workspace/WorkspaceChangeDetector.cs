namespace Capacitor.Cli.Core.Workspace;

/// <summary>
/// What changed in a working tree between two snapshots: a path whose status differs, or which is dirty
/// in both with a different content stamp; a path a commit made during the span names; and a path a stash
/// made during the span names. Rebases, cherry-picks, pulls and merges are not attributed — they replay
/// other people's commits, and whatever of them the agent also changed shows in the status.
/// </summary>
public sealed class WorkspaceChangeDetector(WorkspaceGit git, TimeProvider time, WorkspaceBounds? bounds = null) {
    public const string Create = "create";
    public const string Edit   = "edit";
    public const string Delete = "delete";

    readonly WorkspaceBounds _bounds = bounds ?? WorkspaceBounds.Default;

    public async Task<WorkspaceDetection> DetectAsync(string toplevel, WorkspaceSnapshot before, WorkspaceSnapshot after, TimeSpan budget) {
        var started   = time.GetTimestamp();
        TimeSpan Left() => budget - time.GetElapsedTime(started);

        var hints     = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var truncated = before.Truncated || after.Truncated;

        void Hint(string path, string type) {
            if (!WorkspaceSnapshotter.IsInside(path)) return;
            if (!hints.TryGetValue(path, out var set)) hints[path] = set = [];
            set.Add(type);
        }

        foreach (var path in before.Entries.Keys.Union(after.Entries.Keys, StringComparer.Ordinal)) {
            var was = before.Entries.GetValueOrDefault(path);
            var now = after.Entries.GetValueOrDefault(path);
            StatusHints(toplevel, path, was, now, Hint);
        }

        var history = _bounds.HistoryEntries;

        if (before.HeadReflog >= 0 && after.HeadReflog >= 0 && after.HeadReflog > before.HeadReflog) {
            var gained = after.HeadReflog - before.HeadReflog;
            if (gained > history) truncated = true;
            var read = Math.Min(gained, history);
            history -= read;
            if (!await CommitHintsAsync(toplevel, read, Hint, Left)) truncated = true;
        }

        if (before.StashReflog >= 0 && after.StashReflog >= 0 && after.StashReflog > before.StashReflog) {
            var gained = after.StashReflog - before.StashReflog;
            if (gained > history) truncated = true;
            if (!await StashHintsAsync(toplevel, Math.Min(gained, history), Hint, Left)) truncated = true;
        }

        var changes = hints
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => new WorkspaceChange(h.Key, Resolve(toplevel, h.Key, h.Value)))
            .ToList();

        return new WorkspaceDetection(changes, truncated);
    }

    static void StatusHints(string toplevel, string path, WorkspaceEntry? was, WorkspaceEntry? now, Action<string, string> hint) {
        if (was is null && now is null) return;

        if (was is not null && now is not null && was.Code == now.Code) {
            if (was.Hash is not null && now.Hash is not null && was.Hash != now.Hash) hint(path, Edit);
            return;
        }

        if (now is not null) {
            if (now.OriginalPath is { } from) {
                hint(path, now.Code.Contains('C') ? Create : Edit);
                if (now.Code.Contains('R')) hint(from, Delete);
                return;
            }

            var existedBefore = was is null ? !IsNew(now.Code) : Exists(was.Code);
            hint(path, !Exists(now.Code) ? Delete : existedBefore ? Edit : Create);
            return;
        }

        // Dirty at the start and clean now: reverted, committed, or removed.
        var onDisk = Path.Exists(Path.Combine(toplevel, path));
        hint(path, !onDisk ? Delete : Exists(was!.Code) && !IsNew(was.Code) ? Edit : Create);
    }

    static bool IsNew(string code) => code == "??" || code[0] == 'A';

    static bool Exists(string code) => code[1] != 'D' && !(code[0] == 'D' && code[1] == ' ');

    static string Resolve(string toplevel, string path, HashSet<string> types) {
        if (types.Contains(Delete) && !Path.Exists(Path.Combine(toplevel, path))) return Delete;
        return types.Contains(Create) ? Create : Edit;
    }

    async Task<bool> CommitHintsAsync(string toplevel, int count, Action<string, string> hint, Func<TimeSpan> left) {
        if (count <= 0) return true;

        var log = await git.RunAsync(toplevel, left(), "log", "--walk-reflogs", "-z", "--format=%H%x1f%P%x1f%gs", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "HEAD");
        if (!log.Ok) return false;

        var complete = true;
        foreach (var record in log.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
            var fields = record.Trim('\n').Split('\x1f');
            if (fields.Length < 3 || !fields[2].StartsWith("commit", StringComparison.Ordinal)) continue;

            var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!await TreeHintsAsync(toplevel, parents.FirstOrDefault(), fields[0], hint, left)) complete = false;
        }

        return complete;
    }

    async Task<bool> StashHintsAsync(string toplevel, int count, Action<string, string> hint, Func<TimeSpan> left) {
        if (count <= 0) return true;

        var log = await git.RunAsync(toplevel, left(), "log", "--walk-reflogs", "-z", "--format=%H%x1f%P", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "refs/stash");
        if (!log.Ok) return false;

        var complete = true;
        foreach (var record in log.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
            var fields  = record.Trim('\n').Split('\x1f');
            if (fields.Length < 2) continue;
            var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length == 0) continue;

            if (!await TreeHintsAsync(toplevel, parents[0], fields[0], hint, left)) complete = false;
            // A stash made with --include-untracked keeps the untracked files in a third, parentless commit.
            if (parents.Length > 2 && !await TreeHintsAsync(toplevel, null, parents[2], hint, left)) complete = false;
        }

        return complete;
    }

    async Task<bool> TreeHintsAsync(string toplevel, string? parent, string commit, Action<string, string> hint, Func<TimeSpan> left) {
        string[] args = parent is null
            ? ["diff-tree", "-z", "-r", "-M", "--no-commit-id", "--name-status", "--root", commit]
            : ["diff-tree", "-z", "-r", "-M", "--no-commit-id", "--name-status", parent, commit];

        var diff = await git.RunAsync(toplevel, left(), args);
        if (!diff.Ok || diff.Cut) return false;

        var fields = diff.Stdout.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i++) {
            var status = fields[i];
            if (status.Length == 0) continue;

            switch (status[0]) {
                case 'R' when i + 2 < fields.Length:
                    hint(fields[i + 2], Edit);
                    hint(fields[i + 1], Delete);
                    i += 2;
                    break;
                case 'C' when i + 2 < fields.Length:
                    hint(fields[i + 2], Create);
                    i += 2;
                    break;
                case 'A':
                    hint(fields[++i], Create);
                    break;
                case 'D':
                    hint(fields[++i], Delete);
                    break;
                default:
                    hint(fields[++i], Edit);
                    break;
            }
        }

        return true;
    }
}
