namespace Capacitor.App.Services.CliAdoption;

public enum InstallState { Pending, Left, Switched }

public enum LeftReason {
    OwnFolderOnPath, UnsupervisedDaemon, PromptCancelled, DeletionCutShort, SharedInstallPrivateApp, OwnFolderOnServicePath, SharedInstallInUse,
}

/// <summary>A link on the PATH the app repointed: <paramref name="Original"/> is what it held before.</summary>
public sealed record LinkChange(string Path, string Original, string Replacement);

/// <summary><paramref name="HeldBy"/>, <paramref name="HolderPid"/> and <paramref name="HolderStartToken"/> name what
/// keeps a pending install from being deleted, when something did. For a left install, <paramref name="HeldBy"/> is the
/// <see cref="InstallPlan.Cause"/> that left it.</summary>
public sealed record RecordedInstall(
    OtherCliInstall Install, InstallState State, LeftReason? Reason, string? HeldBy, int? HolderPid, string? HolderStartToken);

/// <summary>An administrator-privileged removal of <paramref name="InstallRoot"/>: <paramref name="GoSent"/> is set
/// before the prompt is answered, so a later launch knows the helper may have run.</summary>
public sealed record PrivilegedOperation(string InstallRoot, int? Pid, string? StartToken, bool GoSent);

public sealed record AdoptionRecord(
    DateTime? CutoverUtc, IReadOnlyList<RecordedInstall> Installs, IReadOnlyList<LinkChange> Links,
    PrivilegedOperation? Privileged, IReadOnlyList<string> NoticesShown, bool MachineSwitched,
    IReadOnlyList<UnprovenMove> UnprovenMoves) {
    public static AdoptionRecord Empty { get; } = new(null, [], [], null, [], false, []);

    /// <summary>Whether an install still waits to be switched. On a machine already switched, only one Finish switching
    /// took back in can.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Pending => Installs.Any(i => i.State == InstallState.Pending);
}
