using Capacitor.Cli.Core;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Decides from fresh evidence what the takeover batch will do. Reads the filesystem, never writes.</summary>
public static class AdoptionPreflight {
    public const string NewerInstall = "newer_install";
    public const string PathUnknown  = "path_unknown";

    public static AdoptionPlan Plan(
            AdoptionEvidence evidence, AdoptionRecord record, string appVersion, string bundleCli, bool appInSystemApplications,
            Func<string, bool> userCanWrite, IReadOnlyList<string> stableFolders) {
        if (evidence.Installs.Count == 0) return new AdoptionPlan(null, [], false, []);

        // A switched machine takes further only what Finish switching took back in: an install its record holds
        // pending, or one that run left for a reason checked again each launch. Any other install found is reported,
        // never removed.
        IReadOnlyList<OtherCliInstall> found =
            record.MachineSwitched ? [.. evidence.Installs.Where(i => Carried(i, record))] : evidence.Installs;
        IReadOnlyList<OtherCliInstall> reported =
            record.MachineSwitched ? [.. evidence.Installs.Where(i => !Carried(i, record))] : [];
        if (found.Count == 0) return new AdoptionPlan(null, [], false, reported);

        var locations = new[] { evidence.KcapOnLoginPath }.Concat(evidence.Units.Select(u => u.KcapOnStoredPath))
            .OfType<string>().DistinctBy(LinkKey, PathComparison.Comparer).ToList();

        var installs = found.Select(i => RecordedLeft(i, record) is { } kept
            ? PlanInstall(i, evidence, locations, bundleCli, appInSystemApplications, userCanWrite) with { Left = kept }
            : PlanInstall(i, evidence, locations, bundleCli, appInSystemApplications, userCanWrite)).ToList();

        var hold = found.Any(i => PrereleaseSemver.IsNewer(i.Version, appVersion)) ? NewerInstall
            : evidence.LoginPath is null                                         ? PathUnknown
                                                                                 : null;

        var eligible = installs.Where(p => p.Left is null).ToList();
        var stable = stableFolders.Select(CanonicalPath.Resolve).ToList();
        var needsStable = eligible.Count > 0 && (locations.Count == 0 || evidence.KcapOnLoginPath is null
            || eligible.SelectMany(p => p.Links).Any(l => !stable.Contains(CanonicalPath.Resolve(Path.GetDirectoryName(l.Path)!), PathComparison.Comparer)));

        return new AdoptionPlan(hold, installs, needsStable, reported);
    }

    static InstallPlan PlanInstall(
            OtherCliInstall install, AdoptionEvidence evidence, List<string> locations, string bundleCli,
            bool appInSystemApplications, Func<string, bool> userCanWrite) {
        var links = new List<LinkChange>();
        string? ownFolderOnPath = null, serviceOnOwnFolder = null;
        foreach (var location in locations.Where(l => Inside(install, l))) {
            if (new FileInfo(location).LinkTarget is { } target) links.Add(new LinkChange(location, target, bundleCli));
            else if (SameLocation(evidence.KcapOnLoginPath, location)) ownFolderOnPath ??= Path.GetDirectoryName(location);
            else serviceOnOwnFolder ??= evidence.Units.FirstOrDefault(u => SameLocation(u.KcapOnStoredPath, location))?.Name;
        }

        var needsAdministrator = install.OwnedEntries.Select(e => Path.GetDirectoryName(e)!).Concat(links.Select(l => Path.GetDirectoryName(l.Path)!))
            .Any(folder => !userCanWrite(folder));

        var unsupervised = UnsupervisedDaemon(install, evidence);
        LeftReason? left = ownFolderOnPath is not null                  ? LeftReason.OwnFolderOnPath
            : serviceOnOwnFolder is not null                            ? LeftReason.OwnFolderOnServicePath
            : unsupervised is not null                                  ? LeftReason.UnsupervisedDaemon
            : needsAdministrator && !appInSystemApplications            ? LeftReason.SharedInstallPrivateApp
                                                                          : null;

        return new InstallPlan(install, left, needsAdministrator, links, ownFolderOnPath ?? serviceOnOwnFolder ?? unsupervised?.Name);
    }

    static bool SameLocation(string? kcap, string location) =>
        kcap is not null && PathComparison.Equal(LinkKey(kcap), LinkKey(location));

    /// <summary>A reason fresh evidence finds again on every launch, and that goes away once the user removes its
    /// cause: an install left for it is planned afresh each time, so the switch finishes once the cause is gone.</summary>
    internal static bool Rederived(LeftReason reason) =>
        reason is LeftReason.OwnFolderOnPath or LeftReason.OwnFolderOnServicePath or LeftReason.UnsupervisedDaemon;

    /// One physical link however the folder holding it is spelled.
    internal static string LinkKey(string location) =>
        Path.Join(CanonicalPath.Resolve(Path.GetDirectoryName(location)!), Path.GetFileName(location));

    /// The record keeps one entry per install folder, however the folder is spelled.
    internal static bool SameRoot(OtherCliInstall a, OtherCliInstall b) =>
        PathComparison.Equal(CanonicalPath.Resolve(a.Root), CanonicalPath.Resolve(b.Root));

    static bool Carried(OtherCliInstall install, AdoptionRecord record) =>
        record.Installs.Any(r =>
            (r.State == InstallState.Pending || r is { State: InstallState.Left, Reason: { } reason } && Rederived(reason))
         && SameRoot(r.Install, install) && r.Install.Identity == install.Identity);

    static LeftReason? RecordedLeft(OtherCliInstall install, AdoptionRecord record) =>
        record.Installs.FirstOrDefault(r => SameRoot(r.Install, install))
            is { State: InstallState.Left, Reason: { } reason } recorded && recorded.Install.Identity == install.Identity && !Rederived(reason)
            ? reason
            : null;

    static bool Inside(OtherCliInstall install, string path) =>
        CanonicalPath.IsWithin(path, install.Root) || install.OwnedEntries.Any(e => CanonicalPath.IsWithin(path, e));

    static RunningDaemonFact? UnsupervisedDaemon(OtherCliInstall install, AdoptionEvidence evidence) =>
        evidence.Daemons.FirstOrDefault(d => d.Executable is { } exe && Inside(install, exe)
            && !evidence.Units.Any(u => u.Loaded && u.JobPid == d.Pid));
}
