using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.App.Services;

/// <summary>
/// Deletes a file, link or directory so a crash at any point leaves something the next launch can finish: the
/// aside name is recorded before the rename, the renamed entry is re-verified and marked so, and only then deleted.
/// A record naming an aside that never came to exist means the rename never happened.
/// </summary>
internal sealed class AsideRetirement(ConfigRoot config, string recordName, Action<string>? step = null) {
    internal const string Renamed = "renamed", Checked = "checked";
    const string Suffix = ".retired-";
    const string Directory_ = "d", Link = "l", File_ = "f";

    // Planned: only the name is recorded, for whoever renames it (the privileged helper checks its own). Unchecked:
    // renamed here, not yet re-verified, so what it holds may be a tree that replaced the one checked earlier.
    const string PlannedState = "planned", UncheckedState = "unchecked", CheckedState = "checked";

    sealed record Planned(string Original, string Aside, string Kind, string State);

    /// <summary>Records the aside name for <paramref name="entry"/> without renaming it.</summary>
    public AsideEntry Plan(string entry) {
        var original = Path.GetFullPath(entry);
        Directory.CreateDirectory(config.Directory);
        using var guard = config.AcquireLock(recordName);
        var planned = Read();
        if (planned.Find(p => p.Original == original) is { } existing) return new AsideEntry(existing.Original, existing.Aside);
        var added = new Planned(original, original + Suffix + Guid.NewGuid().ToString("N"), KindOf(original), PlannedState);
        Write([.. planned, added]);
        return new AsideEntry(added.Original, added.Aside);
    }

    /// <summary>Renames, re-verifies with <paramref name="stillOurs"/> and deletes the entry; a tree that fails the
    /// check is moved back and false is returned. An entry already gone counts as retired.</summary>
    public bool Retire(string entry, Func<string, bool> stillOurs) {
        var plan = Plan(entry);
        if (Exists(plan.Original)) {
            var kind = Recorded(plan.Original)?.Kind ?? KindOf(plan.Original);
            if (Exists(plan.Aside)) throw new IOException($"{plan.Aside} is already in the way.");
            Mark(plan.Original, UncheckedState);
            Move(plan.Original, plan.Aside, kind);
            step?.Invoke(Renamed);
        }
        if (!Exists(plan.Aside)) {
            Forget(plan.Original);
            return true;
        }
        if (!stillOurs(plan.Aside)) {
            if (!Exists(plan.Original)) Move(plan.Aside, plan.Original, KindOf(plan.Aside));
            Forget(plan.Original);
            return false;
        }
        Mark(plan.Original, CheckedState);
        step?.Invoke(Checked);
        DeleteAside(plan.Original, plan.Aside, Recorded(plan.Original)?.Kind ?? KindOf(plan.Aside));
        Forget(plan.Original);
        return true;
    }

    /// <summary>Deletes every recorded aside that passes the guards, except one renamed here and never re-verified,
    /// which goes back to its original name for the next retirement to check; records whose rename never happened are
    /// dropped. Returns the originals whose aside could not be settled: they stay recorded for the next call.</summary>
    public IReadOnlyList<string> FinishInterrupted() {
        var unfinished = new List<string>();
        foreach (var planned in Snapshot()) {
            try {
                var link = planned.Kind == Link;
                if (Exists(planned.Aside) && IsOwnAside(planned.Original, planned.Aside, link)) {
                    if (planned.State != UncheckedState) DeleteAside(planned.Original, planned.Aside, planned.Kind);
                    else if (link && SameLink(planned.Original, planned.Aside)) File.Delete(planned.Aside);
                    else if (Exists(planned.Original)) {
                        unfinished.Add(planned.Original);
                        continue;
                    } else Move(planned.Aside, planned.Original, planned.Kind);
                }
                Forget(planned.Original);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                unfinished.Add(planned.Original);
            }
        }
        return unfinished;
    }

    /// <summary>The name a set-aside entry must carry: beside the original, with the generated suffix, and a link
    /// only when the original was one.</summary>
    internal static bool IsOwnAside(string original, string aside, bool linkAllowed) =>
        Path.GetDirectoryName(aside) == Path.GetDirectoryName(original)
        && Path.GetFileName(aside).StartsWith(Path.GetFileName(original) + Suffix, StringComparison.Ordinal)
        && (linkAllowed || new FileInfo(aside).LinkTarget is null);

    static void DeleteAside(string original, string aside, string kind) {
        if (!IsOwnAside(original, aside, kind == Link)) return;
        if (new FileInfo(aside).LinkTarget is not null) {
            if (kind == Link) File.Delete(aside);
        } else if (kind == Directory_ && Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
        else if (kind == File_ && File.Exists(aside)) File.Delete(aside);
    }

    static string KindOf(string path) =>
        new FileInfo(path).LinkTarget is not null ? Link : Directory.Exists(path) ? Directory_ : File_;

    static bool Exists(string path) => new FileInfo(path).LinkTarget is not null || Path.Exists(path);

    // A link moves as a copy and then the original's removal: one cut short between them leaves two equal links.
    static bool SameLink(string original, string aside) =>
        new FileInfo(original).LinkTarget is { } target && new FileInfo(aside).LinkTarget == target;

    static void Move(string from, string to, string kind) {
        if (kind == Directory_) Directory.Move(from, to);
        else if (kind == Link) {
            // File.Move resolves a link to a directory; recreate the link under the new name, then drop the old one.
            File.CreateSymbolicLink(to, new FileInfo(from).LinkTarget!);
            File.Delete(from);
        } else File.Move(from, to);
    }

    Planned? Recorded(string original) => Snapshot().Find(p => p.Original == original);

    void Mark(string original, string state) {
        using var guard = config.AcquireLock(recordName);
        Write(Read().Select(p => p.Original == original ? p with { State = state } : p));
    }

    List<Planned> Snapshot() {
        using var guard = config.AcquireLock(recordName);
        return Read();
    }

    void Forget(string original) {
        using var guard = config.AcquireLock(recordName);
        var left = Read().Where(p => p.Original != original).ToArray();
        if (left.Length == 0) File.Delete(config.Path(recordName));
        else Write(left);
    }

    List<Planned> Read() {
        string[] lines;
        try {
            lines = File.ReadAllLines(config.Path(recordName));
        } catch (FileNotFoundException) {
            return [];
        }
        var planned = new List<Planned>();
        for (var i = 0; i + 3 < lines.Length; i += 4) {
            if (!Path.IsPathFullyQualified(lines[i]) || !Path.IsPathFullyQualified(lines[i + 1])
                    || lines[i + 2] is not (Directory_ or Link or File_)
                    || lines[i + 3] is not (PlannedState or UncheckedState or CheckedState)) continue;
            planned.Add(new Planned(lines[i], lines[i + 1], lines[i + 2], lines[i + 3]));
        }
        return planned;
    }

    void Write(IEnumerable<Planned> planned) =>
        AtomicFile.Replace(config.Path(recordName),
            string.Concat(planned.Select(p => p.Original + "\n" + p.Aside + "\n" + p.Kind + "\n" + p.State + "\n")),
            mode: UnixFileMode.UserRead | UnixFileMode.UserWrite, followLink: false, durable: true);
}
