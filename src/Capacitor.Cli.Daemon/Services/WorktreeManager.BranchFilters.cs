namespace Capacitor.Cli.Daemon.Services;

/// <summary>Thrown when the filter drivers active for a repository cannot be enumerated or cannot be safely
/// overridden, so it is not known which of them a branch could reach. Fail-closed: the alternative is
/// materialising branch content with every filter live.</summary>
public sealed class BranchFilterInventoryException(string repoPath, string detail)
    : Exception($"Refusing to build a worktree for '{repoPath}': git filter drivers could not be contained. "
              + detail) {
    public string RepoPath { get; } = repoPath;
}

public partial class WorktreeManager {
    /// <summary>
    /// Disable every clean, smudge, and process filter during worktree population: branch-authored
    /// attributes can select an operator-configured shell command that executes branch content.
    /// Names, executable prefixes, and an ambient PATH cannot authenticate an exception, including LFS.
    /// These overrides apply to kcap's git commands only; git run later by the agent uses repo config.
    /// Owned checkouts therefore contain LFS pointers, while borrowed and standalone snapshots copy bytes.
    /// Fail closed if inventory, override encoding, or transport cannot guarantee the filters are disabled.
    /// </summary>
    internal static async Task<GitConfigOverride[]> BranchFilterOverridesAsync(
            string gitContextPath, TimeProvider time) {
        // Enumerate EVERY key and match the shape here, rather than asking git to match a regex.
        //
        // Measured: git's `--get-regexp` runs through the platform regex in the ambient locale, where `.`
        // does not match a byte that is not valid in that encoding. A driver named with a raw 0xff byte —
        // `[filter "ev\xffil"]` — is therefore invisible to `^filter\..*\.(clean|smudge|process)$` while
        // `^filter\.` still finds it, so the inventory came back EMPTY, no override was emitted, and a
        // branch selecting `filter=ev\xffil` in .gitattributes ran the driver. That is precisely the
        // bypass this file exists to prevent, reintroduced by trusting git to enumerate.
        //
        // `--list` takes no pattern, so there is no regex, no locale, and nothing to slip past.
        var listed = await RunGitCaptureResult(gitContextPath, GitTimeout, time, sourceReadOnly: false,
            config: [], "config", "--list", "--name-only", "-z");

        // No "no keys" exit code to tolerate here: `--list` succeeds on an empty config. A non-zero exit
        // means we do not KNOW what is defined, and an empty override set would run the materialisation
        // with every driver live.
        if (listed.ExitCode != 0)
            throw new BranchFilterInventoryException(gitContextPath,
                $"`git config --list` exited {listed.ExitCode}: {listed.Stderr.Trim()}");

        var drivers = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var key in listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = key.Trim().Split('.');
            if (parts.Length < 3) continue;

            // Git lowercases section and variable names but preserves subsection casing; compare accordingly.
            if (!parts[0].Equals("filter", StringComparison.Ordinal)) continue;
            if (parts[^1] is not ("clean" or "smudge" or "process")) continue;

            var driver = string.Join('.', parts[1..^1]);       // a driver name may itself contain dots

            // A name we cannot reproduce is a name we cannot disable. git config is byte-oriented, so a
            // driver name need not be valid UTF-8; decoding one that is not yields U+FFFD, and an override
            // built from that spelling would target a DIFFERENT driver while looking perfectly applied —
            // the same failure as a mis-encoded `=`, and the reason this refuses rather than guesses. A
            // genuine U+FFFD in a driver name is refused too: rare, and fail-closed is the right side.
            if (driver.Contains('\uFFFD'))
                throw new BranchFilterInventoryException(gitContextPath,
                    "A filter driver name is not valid UTF-8, so an override cannot be expressed for it "
                  + "and the driver cannot be contained.");

            // A name containing `=` needs no special handling: overrides travel as key/value PAIRS in the
            // environment, so `filter.evil=x.smudge` arrives whole. It could not be spelled as
            // `-c key=value`, which splits at the first `=` — key `filter.evil`, the real driver still live,
            // the override apparently applied — and was refused for that reason alone. Measured: plain git
            // runs such a driver, `-c` does not stop it, this does.
            drivers.Add(driver);
        }

        return [.. drivers.SelectMany(static driver => new GitConfigOverride[] {
            new($"filter.{driver}.clean", ""),
            new($"filter.{driver}.smudge", ""),
            new($"filter.{driver}.process", ""),
            // Measured: with `required=true`, an empty command is FATAL and the checkout fails outright.
            // Clearing the command alone would turn this guard into a denial of service.
            new($"filter.{driver}.required", "false")
        })];
    }
}
