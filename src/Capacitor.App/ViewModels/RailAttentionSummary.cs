using System.Globalization;

namespace Capacitor.App.ViewModels;

public sealed record RailAttentionSummary(int SessionCount, string Text, string Tone) {
    public static RailAttentionSummary None { get; } = new(0, "", StatusColors.Unavailable);

    public bool HasAttention => Text.Length > 0;
    public bool HasSessionCount => SessionCount > 0;
    public string CountText => SessionCount > 9 ? "9+" : SessionCount.ToString(CultureInfo.InvariantCulture);

    public static RailAttentionSummary Create(int sessions, int failed, RailAlert? alert, string? backgroundPriority, string? reloadFailure) {
        var parts = new List<string>();
        if (sessions > 0) {
            var label = sessions == 1 ? "1 session needs" : $"{sessions} sessions need";
            parts.Add(failed == sessions ? $"{sessions} session{(sessions == 1 ? "" : "s")} failed"
                : failed > 0 ? $"{label} attention ({failed} failed)" : $"{label} you");
        }
        if (alert is not null) parts.Add(alert.Text);
        if (!string.IsNullOrEmpty(backgroundPriority)) parts.Add(backgroundPriority);
        if (!string.IsNullOrEmpty(reloadFailure)) parts.Add(reloadFailure);
        if (parts.Count == 0) return None;
        var warning = sessions > 0 || !string.IsNullOrEmpty(backgroundPriority) || !string.IsNullOrEmpty(reloadFailure);
        var tone = failed > 0 || alert?.Tone == StatusColors.Disrupted ? StatusColors.Disrupted
            : warning ? StatusColors.InProgress : alert?.Tone ?? StatusColors.Unavailable;
        return new(sessions, string.Join(" · ", parts), tone);
    }
}
