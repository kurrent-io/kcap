namespace Capacitor.App.Services.CliAdoption;

/// <summary><paramref name="Links"/> replace each PATH location resolving into the install with a link to the bundle's
/// <c>kcap</c>. <paramref name="Left"/> is set when the install is not switched in this batch.</summary>
public sealed record InstallPlan(OtherCliInstall Install, LeftReason? Left, bool NeedsAdministrator, IReadOnlyList<LinkChange> Links);

/// <summary>A non-null <paramref name="HoldReason"/> means the batch changes nothing. <paramref name="NeedsStableLink"/>
/// is set when a planned link lies outside the stable folders, or no PATH found <c>kcap</c> at all.</summary>
public sealed record AdoptionPlan(
    string? HoldReason, IReadOnlyList<InstallPlan> Installs, bool NeedsStableLink, IReadOnlyList<OtherCliInstall> Reported);
