using Capacitor.App.Services;

namespace Capacitor.App.ViewModels.Driver;

public static class DriverActivities {
    /// <summary>The strongest activity among the live rows whose session is in <paramref name="sessionIds"/>.</summary>
    public static DriverActivity Of(IEnumerable<string> sessionIds, IReadOnlyDictionary<string, AgentRow> liveBySession) {
        var strongest = DriverActivity.None;
        foreach (var id in sessionIds) {
            if (!liveBySession.TryGetValue(id, out var row)) continue;
            var activity = Of(row);
            if (activity > strongest) strongest = activity;
        }
        return strongest;
    }

    public static DriverActivity Of(AgentRow row) {
        if (row.IsEndedSession) return DriverActivity.None;
        if (SessionStatusDots.WaitsOnUser(row) || SessionStatusDots.NeedsAttention(row)) return DriverActivity.WaitingOnYou;
        return SessionStatusDots.IsWorking(row.Status, row.AwaitingInput, row.LiveSubagents) ? DriverActivity.Working : DriverActivity.Idle;
    }

    public static string Label(DriverActivity activity) => activity switch {
        DriverActivity.WaitingOnYou => "Waiting on you",
        DriverActivity.Working      => "Working",
        DriverActivity.Idle         => "Idle",
        _                           => ""
    };

    public static string StatusLabel(string status) => status switch {
        "in_progress" => "In progress",
        "in_review"   => "In review",
        "blocked"     => "Blocked",
        "done"        => "Done",
        _             => "Open"
    };
}
