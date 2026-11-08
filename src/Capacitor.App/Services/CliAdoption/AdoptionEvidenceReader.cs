using System.Xml;
using Capacitor.App.Services.Update;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Reads what the Mac holds of other kcap installs: the daemon service files, the running daemons, the
/// command links and the terminal's <c>PATH</c>. launchd is asked about a service only once an install or a
/// foreign service file turns up, so a Mac with nothing else of kcap never runs <c>launchctl</c>.</summary>
internal sealed class AdoptionEvidenceReader(IProcessRunner runner, ILoginShellProbe probe, DaemonStore store, UserHome home, string bundleRoot,
        IReadOnlyList<string>? linkFolders = null) {
    const string LabelPrefix = "io.kurrent.kcap.daemon.";

    readonly EntryIdentity _identity = new(runner);
    readonly Launchctl     _ctl      = new(runner);

    public async Task<AdoptionEvidence> ReadAsync(CancellationToken ct) {
        var loginPath = probe.TerminalPathAsync(ct);
        var files     = await Task.Run(ReadFiles, ct).ConfigureAwait(false);
        var path      = await loginPath.ConfigureAwait(false);
        var onLogin   = BinaryProbe.Searching(path).Resolve("kcap");

        var candidates = files.Units.Select(u => u.Program)
            .Concat(files.Daemons.Select(d => d.Executable))
            .Concat(files.Links)
            .Append(onLogin)
            .Where(c => c is not null && !CanonicalPath.IsWithin(c, bundleRoot))
            .Select(c => c!);
        var installs = new Dictionary<string, OtherCliInstall>(StringComparer.Ordinal);
        foreach (var candidate in candidates) {
            if (await OtherCliInstallReader.FromBinaryAsync(candidate, _identity, ct).ConfigureAwait(false) is { } install)
                installs.TryAdd(install.Root, install);
        }

        var units = files.Units;
        if (installs.Count > 0 || units.Any(u => !CanonicalPath.IsWithin(u.Program, bundleRoot))) {
            var printed = new List<ServiceUnitFact>();
            foreach (var unit in units) {
                var result = await _ctl.PrintAsync(LabelPrefix + unit.Name, ct).ConfigureAwait(false);
                printed.Add(LaunchdPrint.Classify(result.ExitCode, result.Stdout, result.Stderr) == LabelProbe.Loaded
                    ? unit with { Loaded = true, JobPid = LaunchdPrint.Pid(result.Stdout), LoadedProgram = LaunchdPrint.LoadedProgram(result.Stdout) }
                    : unit);
            }
            units = printed;
        }

        return new AdoptionEvidence([.. installs.Values], units, files.Daemons, path, onLogin) { Links = files.Links };
    }

    sealed record Files(IReadOnlyList<ServiceUnitFact> Units, IReadOnlyList<RunningDaemonFact> Daemons, IReadOnlyList<string> Links);

    Files ReadFiles() => new(ReadUnits(), ReadDaemons(), ReadLinks());

    List<ServiceUnitFact> ReadUnits() {
        var units  = new List<ServiceUnitFact>();
        var agents = Path.Combine(home.Path, "Library", "LaunchAgents");
        if (!Directory.Exists(agents)) return units;

        foreach (var plist in Directory.EnumerateFiles(agents, LabelPrefix + "*.plist").Order(StringComparer.Ordinal)) {
            try {
                var xml     = File.ReadAllText(plist);
                var program = LaunchdPrint.Binary(xml);
                if (program is null) continue;

                var stored = LaunchdPrint.Environment(xml).GetValueOrDefault("PATH");
                var name   = Path.GetFileNameWithoutExtension(plist)[LabelPrefix.Length..];
                units.Add(new ServiceUnitFact(plist, name, program, stored, stored is null ? null : BinaryProbe.Searching(stored).Resolve("kcap"), false, null, null));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException) {
                // A service file that cannot be read says nothing about which kcap it runs.
            }
        }

        return units;
    }

    List<RunningDaemonFact> ReadDaemons() {
        var daemons = new List<RunningDaemonFact>();
        foreach (var name in store.EnumerateNames()) {
            if (DaemonPidLiveness.LivePid(store, name) is not { } pid) continue;
            daemons.Add(new RunningDaemonFact(name, pid, ProcessStartToken.ForPid(pid), ProcessExecutable.Of(pid)));
        }

        return daemons;
    }

    List<string> ReadLinks() => [.. (linkFolders ?? KcapLinkFolders.For(home)).Select(f => Path.Combine(f, "kcap")).Where(File.Exists)];
}
