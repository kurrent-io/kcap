namespace Capacitor.App.ViewModels.NextWork;

/// <summary>The server's launch refusal codes in the web dialog's words.</summary>
public static class NextWorkLaunchText {
    public static string Prepare(string? code) => code switch {
        "unavailable"           => "Starting agents from next work is not available on this server.",
        "target_not_visible"    => "You can no longer see this item.",
        "repo_unknown"          => "This item has no repository to check out.",
        "no_matching_daemon"    => "No connected daemon of yours has a checkout of this repository. Clone it on a machine running kcap daemon.",
        "target_not_launchable" => "This row can't be started.",
        _                       => "Couldn't load launch options.",
    };

    public static string Start(string? code) => code switch {
        "already_in_progress"          => "This work is already reserved or in progress.",
        "loose_end_claims_unavailable" => "Ownership could not be verified. Check server readiness before trying again.",
        "daemon_at_capacity"           => "That daemon has no free agent slots.",
        "vendor_not_offered"           => "That daemon no longer offers this harness.",
        "requested_checkout_unavailable" => "That checkout is no longer available.",
        "target_not_visible"           => "You can no longer see this item.",
        _                              => "Couldn't start the agent.",
    };

    /// <summary>A goal for a row the server builds no prompt for: a pull request to review or land, or an item whose
    /// prompt could not be read, by its label and its own reason (never the facts the band shows in its place).</summary>
    public static string Goal(NextWorkRow row, string label, string? url) {
        var link = url is null ? "" : $" ({url})";
        return row.TargetKind switch {
            "pr" when row.Arm == "ReviewRequested"  => $"Review pull request {label}{link}.",
            "pr" when row.Arm == "ApprovedUnmerged" => $"Get pull request {label}{link} ready to merge.",
            "pr"                                    => $"Pick up pull request {label}{link}.",
            _                                       => NextWorkLaneLogic.BecauseIsLabel(row) ? $"{label}{link}" : $"{label}{link}\n\n{row.Because.Trim()}",
        };
    }
}
