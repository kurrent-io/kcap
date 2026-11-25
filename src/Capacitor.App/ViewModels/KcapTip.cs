using System.Globalization;
using static Capacitor.App.ViewModels.KcapTipDetail;

namespace Capacitor.App.ViewModels;

public sealed record KcapTip(string Title, string? State = null, string? Tone = null,
        IReadOnlyList<KcapTipDetail>? Details = null) {
    public string? StateDetail { get; init; }
    public bool HasDetails => Details is { Count: > 0 };
    public bool IsDanger => Tone == "danger";
    public bool IsActive => Tone == "active";
    public bool IsPrOpen => Tone == "prOpen";
    public bool IsPrMerged => Tone == "prMerged";
    public bool IsPrClosed => Tone == "prClosed";

    public string Description => string.Join("\n", new[] { Title, State, StateDetail }
        .Concat((Details ?? []).Select(row => row.Description)).Where(line => !string.IsNullOrWhiteSpace(line)));

    public static KcapTip Session(AgentStatusPresentation status, string? title, string? machine, string? checkout) {
        var staleFact = status.Kind == AgentStatusKind.Stale && status.Facts is [{ HasCaption: false } first, ..]
            ? first : null;
        var details = status.Facts
            .SelectMany(fact => Present(
                !fact.HasCaption && (fact.Text == status.Label || ReferenceEquals(fact, staleFact))
                    ? null : Field(fact.Caption, fact.Text),
                Note(fact.Detail)))
            .Concat(Present(Field("Remote daemon", machine), Field("Checkout", checkout)))
            .ToArray();
        var tone = status.IsDanger ? "danger" : status.IsInFlight ? "active" : null;
        return new(string.IsNullOrWhiteSpace(title) ? "Session" : title, status.Label, tone, details) {
            StateDetail = staleFact?.Text,
        };
    }

    public static KcapTip PullRequest(int number, string? title, PullRequestStatus? lifecycle,
            string? repository, KcapTipDetail? context = null) {
        var numberLabel = "#" + number.ToString(CultureInfo.InvariantCulture);
        title = string.IsNullOrWhiteSpace(title) ? null : title;
        var reference = title is null ? Field("Repository", repository)
            : Field("Pull request", string.IsNullOrWhiteSpace(repository) ? numberLabel : $"{repository} {numberLabel}");
        return new(title ?? $"Pull request {numberLabel}",
            lifecycle?.Text,
            lifecycle?.Kind switch {
                "open" => "prOpen",
                "merged" => "prMerged",
                "closed" => "prClosed",
                _ => lifecycle is { IsDanger: true } ? "danger" : null,
            },
            Present(reference, context));
    }

    public static string? Join(string? label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : string.IsNullOrWhiteSpace(label) ? value : $"{label} · {value}";
}
