using Capacitor.App.Services;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.App.ViewModels;

/// One account as the Accounts tab shows it. A null Id is the daemon's default directory with no
/// registry entry behind it, whose one action is Add.
public sealed record AccountSettingsRow(string? Id, string Vendor, string Directory, string Label, string RecordingState, string? Email, string? Plan, string? ErrorText, AccountUsageSlot Usage, bool IsDefault, string? SignIn = null) {
    bool SignedOut => SignIn == SignInStates.SignedOut;

    public string StateLabel => SignedOut ? "Needs sign-in" : RecordingStateLabels.Label(Vendor, RecordingState);
    public bool StateIsAttention => SignedOut || RecordingStateLabels.IsAttention(Vendor, RecordingState);
    public bool StateIsHealthy => !SignedOut && RecordingStateLabels.IsHealthy(Vendor, RecordingState);
    public string? StateHint => IsUnregisteredDefault ? null
        : SignedOut ? "Signed out. Sign in to this account again so its sessions record and its usage reads."
        : RecordingStateLabels.Hint(Vendor, RecordingState);
    public string? StateCommand => SignedOut ? null : RecordingStateLabels.Command(Vendor, RecordingState, Directory);
    public bool IsUnregisteredDefault => Id is null;
    /// A state with something to explain gets the banner below the usage instead of a badge.
    public bool ShowsStateBadge => StateHint is null;
    public bool CanReconnect => !SignedOut && !IsUnregisteredDefault && RecordingStateLabels.NeedsReconnect(RecordingState);

    /// The label defaults to the email, and showing it twice reads as two accounts.
    public bool ShowsEmail => !string.IsNullOrEmpty(Email) && !string.Equals(Email, Label, StringComparison.OrdinalIgnoreCase);

    /// Vendors report the plan in lower case ("pro", "max", "plus").
    public string? PlanLabel => string.IsNullOrWhiteSpace(Plan) ? null : Plan.Trim().ToUpperInvariant();
}
