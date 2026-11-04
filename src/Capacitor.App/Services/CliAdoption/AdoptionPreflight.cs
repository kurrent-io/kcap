using Capacitor.Cli.Core;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Decides from fresh evidence what the takeover batch will do. Reads the filesystem, never writes.</summary>
public static class AdoptionPreflight {
    public const string NewerInstall                = "newer_install";
    public const string PathUnknown                 = "path_unknown";
    public const string AdministratorSupportPending = "administrator_support_pending";

    public static AdoptionPlan Plan(
            AdoptionEvidence evidence, AdoptionRecord record, string appVersion, string bundleCli, bool appInSystemApplications,
            Func<string, bool> userCanWrite, IReadOnlyList<string> stableFolders) {
        if (evidence.Installs.Count == 0) return new AdoptionPlan(null, [], false, []);
        if (record.MachineSwitched) return new AdoptionPlan(null, [], false, evidence.Installs);

        var locations = new[] { evidence.KcapOnLoginPath }.Concat(evidence.Units.Select(u => u.KcapOnStoredPath))
            .OfType<string>().DistinctBy(LinkKey, PathComparison.Comparer).ToList();

        var installs = evidence.Installs.Select(i => RecordedLeft(i, record) is { } kept
            ? PlanInstall(i, evidence, locations, bundleCli, appInSystemApplications, userCanWrite) with { Left = kept }
            : PlanInstall(i, evidence, locations, bundleCli, appInSystemApplications, userCanWrite)).ToList();

        var hold = evidence.Installs.Any(i => PrereleaseSemver.IsNewer(i.Version, appVersion)) ? NewerInstall
            : evidence.LoginPath is null                                                       ? PathUnknown
            : installs.Any(p => p.Left is null && p.NeedsAdministrator)                        ? AdministratorSupportPending
                                                                                               : null;

        var eligible = installs.Where(p => p.Left is null).ToList();
        var stable = stableFolders.Select(CanonicalPath.Resolve).ToList();
        var needsStable = eligible.Count > 0 && (locations.Count == 0 || evidence.KcapOnLoginPath is null
            || eligible.SelectMany(p => p.Links).Any(l => !stable.Contains(CanonicalPath.Resolve(Path.GetDirectoryName(l.Path)!), PathComparison.Comparer)));

        return new AdoptionPlan(hold, installs, needsStable, []);
    }

    static InstallPlan PlanInstall(
            OtherCliInstall install, AdoptionEvidence evidence, List<string> locations, string bundleCli,
            bool appInSystemApplications, Func<string, bool> userCanWrite) {
        var links = new List<LinkChange>();
        var ownFolderOnPath = false;
        foreach (var location in locations.Where(l => Inside(install, l))) {
            if (new FileInfo(location).LinkTarget is { } target) links.Add(new LinkChange(location, target, bundleCli));
            else ownFolderOnPath = true;
        }

        var needsAdministrator = install.OwnedEntries.Select(e => Path.GetDirectoryName(e)!).Concat(links.Select(l => Path.GetDirectoryName(l.Path)!))
            .Any(folder => !userCanWrite(folder));

        LeftReason? left = ownFolderOnPath                              ? LeftReason.OwnFolderOnPath
            : HasUnsupervisedDaemon(install, evidence)                  ? LeftReason.UnsupervisedDaemon
            : needsAdministrator && !appInSystemApplications            ? LeftReason.SharedInstallPrivateApp
                                                                          : null;

        return new InstallPlan(install, left, needsAdministrator, links);
    }

    static string LinkKey(string location) =>
        Path.Join(CanonicalPath.Resolve(Path.GetDirectoryName(location)!), Path.GetFileName(location));

    static LeftReason? RecordedLeft(OtherCliInstall install, AdoptionRecord record) =>
        record.Installs.FirstOrDefault(r => PathComparison.Equal(CanonicalPath.Resolve(r.Install.Root), CanonicalPath.Resolve(install.Root)))
            is { State: InstallState.Left, Reason: { } reason } recorded && recorded.Install.Identity == install.Identity
            ? reason
            : null;

    static bool Inside(OtherCliInstall install, string path) =>
        CanonicalPath.IsWithin(path, install.Root) || install.OwnedEntries.Any(e => CanonicalPath.IsWithin(path, e));

    static bool HasUnsupervisedDaemon(OtherCliInstall install, AdoptionEvidence evidence) =>
        evidence.Daemons.Any(d => d.Executable is { } exe && Inside(install, exe)
            && !evidence.Units.Any(u => u.Loaded && u.JobPid == d.Pid));
}
