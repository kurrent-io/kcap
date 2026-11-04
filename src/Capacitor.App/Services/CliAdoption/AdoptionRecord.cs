namespace Capacitor.App.Services.CliAdoption;

public enum InstallState { Pending, Left, Switched }

public enum LeftReason { OwnFolderOnPath, UnsupervisedDaemon, PromptCancelled, DeletionCutShort, SharedInstallPrivateApp }

/// <summary>A link on the PATH the app repointed: <paramref name="Original"/> is what it held before.</summary>
public sealed record LinkChange(string Path, string Original, string Replacement);

/// <summary><paramref name="HeldBy"/>, <paramref name="HolderPid"/> and <paramref name="HolderStartToken"/> name the
/// process that kept the install from being switched, when one did.</summary>
public sealed record RecordedInstall(
    OtherCliInstall Install, InstallState State, LeftReason? Reason, string? HeldBy, int? HolderPid, string? HolderStartToken);

/// <summary>An administrator-privileged removal of <paramref name="InstallRoot"/>: <paramref name="GoSent"/> is set
/// before the prompt is answered, so a later launch knows the helper may have run.</summary>
public sealed record PrivilegedOperation(string InstallRoot, int? Pid, string? StartToken, bool GoSent);

public sealed record AdoptionRecord(
    DateTime? CutoverUtc, IReadOnlyList<RecordedInstall> Installs, IReadOnlyList<LinkChange> Links,
    PrivilegedOperation? Privileged, IReadOnlyList<string> NoticesShown, bool MachineSwitched) {
    public static AdoptionRecord Empty { get; } = new(null, [], [], null, [], false);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Pending => !MachineSwitched && Installs.Any(i => i.State == InstallState.Pending);
}
