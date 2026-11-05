using System.ComponentModel;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.LocalSetup;

/// <summary>
/// Brings every kcap the local installer would refuse up to <see cref="LocalClientFloor"/> before the install starts,
/// so the user is never asked to: an npm install is updated through npm, and anything else on the terminal's
/// <c>PATH</c> becomes a link to this app's own kcap, which always meets the floor. Windows has no administrator
/// relink, so there anything npm cannot update is left for the user.
/// </summary>
public sealed class StaleClientRepair(
        IProcessRunner runner, ILoginShellProbe probe, string? appCli, Func<string?, ILocalClientCopies> copies) {
    const string OsascriptPath = "/usr/bin/osascript";
    const string NpmPackage    = OtherCliInstallReader.NpmPackage;

    readonly EntryIdentity _identity = new(runner);

    static readonly TimeSpan NpmTimeout = TimeSpan.FromMinutes(5);

    /// Each repair fixes the copy PATH finds first, which can uncover another behind it, and an entry that changed
    /// before it could be replaced is looked at again.
    const int MaxAttempts = 6;

    /// Printed by the administrator script when the entry is no longer the one that was found.
    const string EntryChanged = "kcap-entry-changed";

    public static StaleClientRepair ForApp(IProcessRunner runner, ILoginShellProbe probe, string? appCli, UserHome home) =>
        new(runner, probe, appCli, path => new LocalClientCopies(runner, path, appCli, LocalClientCopies.AppClis(home)));

    public async Task<ClientRepairOutcome> RunAsync(CancellationToken ct) {
        var path = await probe.TerminalPathAsync(ct).ConfigureAwait(false) ?? Environment.GetEnvironmentVariable("PATH");
        var npmTried = new HashSet<string>(StringComparer.Ordinal);
        var npmFailed = new Dictionary<string, string>(StringComparer.Ordinal);
        var relinked = new HashSet<string>(StringComparer.Ordinal);
        string? repaired = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++) {
            var stale = await FindStaleAsync(path, ct).ConfigureAwait(false);

            if (stale is null) return repaired is null ? new ClientRepairOutcome.Clear() : new ClientRepairOutcome.Repaired(repaired);
            if (stale.Kind == ClientCopyKind.DesktopApp) return new ClientRepairOutcome.OldApp(stale.Path);
            if (relinked.Contains(stale.Path)) return new ClientRepairOutcome.Failed(stale.Path, "it still reports an older version after it was replaced.");

            // Found again either way: npm may have left anything at that entry, updated or not.
            if (OtherCliInstallReader.NpmPrefixOf(stale.Path) is { } prefix && npmTried.Add(stale.Path)) {
                var (updated, why) = await UpdateThroughNpmAsync(prefix, path, ct).ConfigureAwait(false);
                if (updated) repaired = stale.Path;
                else npmFailed[stale.Path] = why;
                continue;
            }

            // An npm install npm could not update says why in npm's words; there is no other repair to fall back to.
            if (OperatingSystem.IsWindows())
                return npmFailed.TryGetValue(stale.Path, out var npmWhy) ? new ClientRepairOutcome.Failed(stale.Path, $"npm could not update it ({npmWhy}).")
                    : npmTried.Contains(stale.Path) ? new ClientRepairOutcome.StillOld(stale.Path, stale.Version)
                    : new ClientRepairOutcome.Unrepairable(stale.Path);

            if (await BoundIdentityAsync(path, stale.Path, ct).ConfigureAwait(false) is not { } found) continue;

            switch (await RelinkAsync(stale.Path, found, ct).ConfigureAwait(false)) {
                case Relink.Changed:
                    continue;
                case Relink.Refused(var refused):
                    return refused;
            }

            relinked.Add(stale.Path);
            repaired = stale.Path;
        }

        return new ClientRepairOutcome.Failed(repaired ?? "", "more older copies of kcap turned up than setup can replace.");
    }

    Task<ClientCopy?> FindStaleAsync(string? path, CancellationToken ct) =>
        Task.Run(() => copies(path).Find().FirstOrDefault(c => !LocalClientFloor.AdmitsOn(OperatingSystem.IsWindows(), c.Version)), ct);

    /// The entry's identity, read before a probe that still finds it below the floor, so the version that probe reports
    /// belongs to the entry the identity describes: the replacement checks against it.
    async Task<string?> BoundIdentityAsync(string? path, string entry, CancellationToken ct) {
        if (await _identity.OfAsync(entry, ct).ConfigureAwait(false) is not { } before) return null;

        return await FindStaleAsync(path, ct).ConfigureAwait(false) is { } stale && stale.Path == entry ? before : null;
    }

    abstract record Relink {
        public sealed record Done : Relink;
        public sealed record Changed : Relink;
        public sealed record Refused(ClientRepairOutcome Outcome) : Relink;
    }

    /// Runs the npm of the prefix that owns the entry: another npm earlier on PATH would update a different install
    /// and leave this one older. On Windows npm usually sits with node instead, so the one on PATH is told the prefix.
    async Task<(bool Updated, string Why)> UpdateThroughNpmAsync(string prefix, string? searchPath, CancellationToken ct) {
        var windows = OperatingSystem.IsWindows();
        var bin     = windows ? prefix : Path.Combine(prefix, "bin");
        var npm     = BinaryProbe.Searching(bin).Resolve("npm") ?? (windows ? BinaryProbe.Searching(searchPath).Resolve("npm") : null);
        if (npm is null) return (false, "npm was not found");

        // npm is a node script, and its own folder is where a node install keeps node.
        var head = windows ? Path.GetDirectoryName(npm)! : bin;
        var env  = new Dictionary<string, string> { ["PATH"] = string.IsNullOrEmpty(searchPath) ? head : $"{head}{Path.PathSeparator}{searchPath}" };
        string[] args = windows
            ? ["install", "--global", "--prefix", prefix, $"{NpmPackage}@latest"]
            : ["install", "--global", $"{NpmPackage}@latest"];
        try {
            var result = await runner.RunAsync(npm, args, new RunOptions(env, NpmTimeout), ct)
                .ConfigureAwait(false);
            return result is { ExitCode: 0, TimedOut: false } ? (true, "")
                : (false, result.TimedOut ? "npm timed out" : NpmErrorOf(result.Stderr) ?? $"npm exited {result.ExitCode}");
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) {
            return (false, ex.Message);
        }
    }

    /// npm's own summary of a failed install, such as "EEXIST: file already exists": the first error line that is not
    /// one of its key-value lines or the pointer to its debug log.
    internal static string? NpmErrorOf(string stderr) =>
        stderr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("npm error ", StringComparison.Ordinal) || l.StartsWith("npm ERR! ", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(' ', 4) + 1)..].Trim())
            .FirstOrDefault(l => l.Length > 0 && !NpmErrorKeys.Any(k => l.StartsWith(k, StringComparison.Ordinal)));

    static readonly string[] NpmErrorKeys = ["code ", "path ", "syscall ", "errno ", "dest ", "A complete log"];

    /// Whether this app's kcap is one the installer admits: linking PATH to anything else, a missing file included,
    /// would trade one refused copy for another.
    async Task<bool> TargetAdmittedAsync(CancellationToken ct) =>
        appCli is not null && await Task.Run(() => LocalClientFloor.Admits(LocalClientCopies.VersionOf(runner, appCli)), ct).ConfigureAwait(false);

    /// Replaces <paramref name="link"/> only while it is still the entry <paramref name="found"/> describes: anything
    /// that took its place in the meantime is someone else's and is looked at again. Neither rename nor ln can compare
    /// as it replaces, so an entry landing between the last check and the swap still loses, to this app's kcap.
    async Task<Relink> RelinkAsync(string link, string found, CancellationToken ct) {
        if (appCli is null || !OneLine(appCli) || !OneLine(link) || !OneLine(found) || !await TargetAdmittedAsync(ct).ConfigureAwait(false))
            return new Relink.Refused(new ClientRepairOutcome.Failed(link, "this app has no up-to-date kcap of its own to put there."));

        switch (await TryReplaceAsync(link, appCli, found, ct).ConfigureAwait(false)) {
            case true:  return new Relink.Done();
            case false: return new Relink.Changed();
        }

        ProcessResult result;
        try {
            result = await runner.RunAsync(OsascriptPath, OsascriptArgs(appCli, link, found), new RunOptions(CancelMode: CancelMode.KillTree), ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) {
            return new Relink.Refused(new ClientRepairOutcome.Failed(link, ex.Message));
        }

        if (result.ExitCode == 0) return new Relink.Done();
        if (result.Stderr.Contains(EntryChanged, StringComparison.Ordinal)) return new Relink.Changed();
        if (result.Stderr.Contains("(-128)", StringComparison.Ordinal)) return new Relink.Refused(new ClientRepairOutcome.Cancelled(link));

        return new Relink.Refused(new ClientRepairOutcome.Failed(
            link, string.IsNullOrWhiteSpace(result.Stderr) ? "osascript failed." : result.Stderr.Trim()));
    }

    /// A line break would desynchronise osascript's argv from the script reading it.
    static bool OneLine(string path) => !path.Contains('\n') && !path.Contains('\r');

    /// Without an administrator prompt, where the folder is this user's to write: a link made beside the old copy is
    /// renamed over it, so PATH never goes without a kcap. True when replaced, false when the entry changed, null
    /// when the folder needs an administrator.
    async Task<bool?> TryReplaceAsync(string link, string target, string found, CancellationToken ct) {
        try {
            return await KcapLinkSwap.ReplaceAsync(
                link, target, async token => await _identity.OfAsync(link, token).ConfigureAwait(false) == found, ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// The paths and the expected identity travel as argv and are read back through <c>quoted form</c>, never spliced
    /// into the script. The identity is compared in the same privileged shell that replaces the entry.
    internal static string[] OsascriptArgs(string target, string link, string found) => [
        "-e", "on run argv",
        "-e", "do shell script \"[ \\\"$(" + EntryIdentity.StatPath + " -f " + EntryIdentity.StatArgs[1] + " \" & quoted form of item 2 of argv & \")\\\" = \" & quoted form of item 3 of argv & \" ] || { echo " + EntryChanged + " >&2; exit 3; }; /bin/ln -sfh \" & quoted form of item 1 of argv & \" \" & quoted form of item 2 of argv with administrator privileges",
        "-e", "end run",
        "--", target, link, found,
    ];
}
