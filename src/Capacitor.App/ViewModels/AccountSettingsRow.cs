using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// One account as the Accounts tab shows it. A null Id is the daemon's default directory with no
/// registry entry behind it, whose one action is Add.
public sealed record AccountSettingsRow(string? Id, string Vendor, string Directory, string Label, string RecordingState, string? Email, string? Plan, string? ErrorText, AccountUsageSlot Usage, bool IsDefault) {
    public string StateLabel => RecordingStateLabels.Label(Vendor, RecordingState);
    public bool StateIsAttention => RecordingStateLabels.IsAttention(Vendor, RecordingState);
    public bool StateIsHealthy => RecordingStateLabels.IsHealthy(Vendor, RecordingState);
    public string? StateHint => IsUnregisteredDefault ? null : RecordingStateLabels.Hint(Vendor, RecordingState, Directory);
    public string? StateCommand => RecordingStateLabels.Command(Vendor, RecordingState, Directory);
    public bool IsUnregisteredDefault => Id is null;
    /// A state with something to explain gets the banner below the usage instead of a badge.
    public bool ShowsStateBadge => StateHint is null;
    public bool CanReconnect => !IsUnregisteredDefault && RecordingStateLabels.NeedsReconnect(RecordingState);

    /// The label defaults to the email, and showing it twice reads as two accounts.
    public bool ShowsEmail => !string.IsNullOrEmpty(Email) && !string.Equals(Email, Label, StringComparison.OrdinalIgnoreCase);

    /// Vendors report the plan in lower case ("pro", "max", "plus").
    public string? PlanLabel => string.IsNullOrWhiteSpace(Plan) ? null : Plan.Trim().ToUpperInvariant();
}
