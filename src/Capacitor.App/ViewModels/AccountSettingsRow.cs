using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// One account as the Accounts tab shows it. A null Id is the daemon's default directory with no
/// registry entry behind it, whose one action is Add.
public sealed record AccountSettingsRow(string? Id, string Vendor, string Directory, string Label, string RecordingState, string? Email, string? Plan, string? ErrorText, AccountUsageSlot Usage, bool IsDefault, string? SignIn = null) {
    string? Fix => AccountUsage.NeedsFix(SignIn, Vendor, RecordingState);
    bool SignedOut => Fix == AccountUsage.NeedsSignIn;
    public string StateLabel => Fix ?? RecordingStateLabels.Label(Vendor, RecordingState);
    public bool StateIsAttention => Fix is not null;
    public bool StateIsHealthy => Fix is null && RecordingStateLabels.IsHealthy(Vendor, RecordingState);
    public string? StateHint => SignedOut
        ? "Signed out. Sign in to this account again so its sessions record and its usage reads."
        : RecordingStateLabels.Hint(Vendor, RecordingState, Directory);
    public string? StateCommand => SignedOut ? null : RecordingStateLabels.Command(Vendor, RecordingState, Directory);
    public bool IsUnregisteredDefault => Id is null;
}
