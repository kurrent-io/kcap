namespace Capacitor.Cli.Core;

/// <summary>
/// Store a per-vendor, operator-affirmed minimum build. Containment relies on vendor behavior outside
/// this repository; upgrades are admitted, while raising the floor excludes a known bad build.
/// Dotfiles must not silently reaffirm the record. Antigravity seeds its floor on binary availability
/// because the same floor gates owner-requested hosting; later reviewer consent must not reseed it.
/// Retain Kiro's persisted filename so an upgrade does not discard existing affirmations.
/// </summary>
public sealed class ReviewerVersionStore(string stateDir, string vendor) {
    public static string FileNameFor(string vendor) => $"{vendor}-reviewer-affirmed-version";

    readonly string _path = Path.Combine(stateDir, FileNameFor(vendor));

    /// <summary>Whether the record is PRESENT, whatever its content. Distinct from
    /// <see cref="Affirmed"/> being non-null: a corrupt or unreadable record exists but affirms
    /// nothing, and conflating the two lets a deleted record be silently re-seeded.</summary>
    public static bool RecordExists(string stateDir, string vendor) =>
        Path.Exists(Path.Combine(stateDir, FileNameFor(vendor)));

    /// <summary>
    /// The affirmed version, or <see langword="null"/> when none has been recorded. Never throws:
    /// missing, unreadable, or a directory sitting at the pathname all read as "not affirmed", which
    /// is the fail-closed direction, and a daemon boot must not brick on this file.
    /// </summary>
    public string? Affirmed {
        get {
            try {
                // FileShare.ReadWrite, not File.ReadAllText: the `kcap daemon reviewer affirm` verb
                // writes this file from a DIFFERENT process while a daemon may be reading it, and a
                // write-denying open blocks that writer outright on platforms with mandatory sharing.
                using var stream = new FileStream(
                    _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                var text = reader.ReadToEnd().Trim();
                return text.Length == 0 ? null : text;
            } catch {
                return null;
            }
        }
    }

    public void Affirm(string version) {
        Directory.CreateDirectory(stateDir);

        // Mode set BEFORE any content exists, as LaunchConsentStore does for the same reason: a chmod
        // after the write leaves a readable window, however brief.
        var options = new FileStreamOptions {
            // ReadWrite, not None: sharing is BIDIRECTIONAL on Windows, so a writer that denies
            // readers cannot open while a daemon holds the file for reading. Fixing only the reader
            // left the affirm verb failing from the other side of the same race.
            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.ReadWrite
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(_path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(version.Trim());
    }
}

/// <summary>Whether the installed build clears the recorded minimum.</summary>
public enum ReviewerVersionAffirmation {
    /// <summary>Installed is at or above the recorded minimum — allowed.</summary>
    MeetsMinimum,
    /// <summary>The installed build could not be identified — denied, since it cannot be compared.</summary>
    Unresolved,
    /// <summary>No minimum recorded at all. Distinct from <see cref="BelowMinimum"/> because the
    /// operator's remedy differs: record one, rather than change the installed build.</summary>
    NoMinimumRecorded,
    /// <summary>Installed is older than the recorded minimum.</summary>
    BelowMinimum,
    /// <summary>Exactly one of the two values orders as a version and the other does not, so there is
    /// no ordering to apply. Denied — but see <see cref="ReviewerVersionAffirmations"/> for why the
    /// affirm remedy provably terminates rather than looping.</summary>
    Incomparable
}

/// <summary>
/// The recorded value is the oldest admitted build, not an exact match: upgrades remain eligible.
/// A vendor upgrade can weaken containment without this gate detecting it; the operator raises the
/// floor to exclude a bad build. Only this comparison is shared; vendors retain their own platform,
/// consent, and containment requirements.
/// </summary>
public static class ReviewerVersionAffirmations {
    /// <summary>
    /// Pure. An ORDERED cascade, not a match over two parse results — see the inline note on why the
    /// null checks must come first.
    ///
    /// <para>Both sides are trimmed because a vendor's <c>--version</c> output and a hand-written
    /// record both carry stray whitespace.</para>
    /// </summary>
    public static ReviewerVersionAffirmation Decide(string? installedVersion, string? minimumVersion) {
        // Rows 1-2 FIRST, and this ordering is load-bearing: Version.TryParse(null) returns false
        // silently, so parsing both sides up front and matching on the two results would route a
        // MISSING record into Incomparable/BelowMinimum instead of NoMinimumRecorded — the wrong
        // denial for the most common misconfiguration (a reviewer enabled against an already-running
        // daemon, which has not yet had a startup to seed one).
        if (Normalize(installedVersion) is not { } installed) return ReviewerVersionAffirmation.Unresolved;
        if (Normalize(minimumVersion) is not { } minimum)     return ReviewerVersionAffirmation.NoMinimumRecorded;

        var installedVersionParsed = TryParseVersion(installed);
        var minimumVersionParsed   = TryParseVersion(minimum);

        // Unorderable version strings require ordinal equality, including equal values no parser accepts.
        if (installedVersionParsed is null && minimumVersionParsed is null)
            return string.Equals(installed, minimum, StringComparison.Ordinal)
                ? ReviewerVersionAffirmation.MeetsMinimum
                : ReviewerVersionAffirmation.BelowMinimum;

        // Exactly one orders. Ordinal equality is only sound when both values are in the same domain;
        // comparing across domains would refuse a genuine upgrade while LABELLING it "below minimum".
        // Say we cannot order them instead. The affirm remedy terminates either way: if the minimum
        // is the odd one, affirming writes the (orderable) installed value; if the installed one is
        // odd, affirming makes both that same odd string, which the ordinal branch above then allows.
        if (installedVersionParsed is null || minimumVersionParsed is null)
            return ReviewerVersionAffirmation.Incomparable;

        return installedVersionParsed >= minimumVersionParsed
            ? ReviewerVersionAffirmation.MeetsMinimum
            : ReviewerVersionAffirmation.BelowMinimum;
    }

    /// <summary>
    /// Reviewer and daemon gates must classify orderable versions identically.
    /// Do not trim before TrimStart('v','V'): Version.TryParse permits surrounding whitespace, but an
    /// extra trim would expose the v in " v1.2.3" and admit a form the certification gate rejects.
    /// </summary>
    public static Version? TryParseVersion(string? version) =>
        Version.TryParse((version ?? "").TrimStart('v', 'V').Split('-', '+')[0], out var parsed)
            ? parsed
            : null;

    /// <summary>Trimmed, or null when there is nothing there — whitespace-only is not a version.</summary>
    public static string? Normalize(string? version) =>
        version is { Length: > 0 } v && v.Trim() is { Length: > 0 } t ? t : null;

    /// <summary>Render for an operator-facing message.</summary>
    public static string Describe(string? version) => Normalize(version) ?? "<none>";
}
