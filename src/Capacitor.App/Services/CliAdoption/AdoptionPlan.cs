namespace Capacitor.App.Services.CliAdoption;

/// <summary><paramref name="Links"/> replace each PATH location resolving into the install with a link to the bundle's
/// <c>kcap</c>. <paramref name="Left"/> is set when the install is not switched in this batch, and
/// <paramref name="Cause"/> names what left it, where the user can remove that: the PATH folder for
/// <see cref="LeftReason.OwnFolderOnPath"/>, the daemon's name for <see cref="LeftReason.UnsupervisedDaemon"/>.</summary>
public sealed record InstallPlan(
    OtherCliInstall Install, LeftReason? Left, bool NeedsAdministrator, IReadOnlyList<LinkChange> Links, string? Cause = null);

/// <summary>A non-null <paramref name="HoldReason"/> means the batch changes nothing. <paramref name="NeedsStableLink"/>
/// is set when a planned link lies outside the stable folders, or no PATH found <c>kcap</c> at all.</summary>
public sealed record AdoptionPlan(
    string? HoldReason, IReadOnlyList<InstallPlan> Installs, bool NeedsStableLink, IReadOnlyList<OtherCliInstall> Reported);
