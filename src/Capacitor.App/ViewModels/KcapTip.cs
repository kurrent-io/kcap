using System.Globalization;
using static Capacitor.App.ViewModels.KcapTipDetail;

namespace Capacitor.App.ViewModels;

public sealed record KcapTip(string Title, string? State = null, string? Tone = null,
        IReadOnlyList<KcapTipDetail>? Details = null, string? Action = null) {
    public bool HasDetails => Details is { Count: > 0 };
    public bool IsDanger => Tone == "danger";
    public bool IsActive => Tone == "active";

    public string Description => string.Join("\n", new[] { Title, State }
        .Concat((Details ?? []).Select(row => row.Description)).Append(Action).Where(line => !string.IsNullOrWhiteSpace(line)));

    public static KcapTip Session(AgentStatusPresentation status, string? title, string? machine, string? checkout) {
        var details = status.Facts
            .SelectMany(fact => Present(
                fact.Text == status.Label && !fact.HasCaption ? null : Field(fact.Caption, fact.Text), Note(fact.Detail)))
            .Concat(Present(Field("Remote daemon", machine), Field("Checkout", checkout)))
            .ToArray();
        var tone = status.IsDanger ? "danger" : status.IsInFlight ? "active" : null;
        return new(string.IsNullOrWhiteSpace(title) ? "Session" : title, status.Label, tone, details, "Open session");
    }

    public static KcapTip PullRequest(int number, string? title, PullRequestStatus? lifecycle,
            string? repository, string? action, KcapTipDetail? context = null, string? checks = null) {
        var numberLabel = "#" + number.ToString(CultureInfo.InvariantCulture);
        title = string.IsNullOrWhiteSpace(title) ? null : title;
        var reference = title is null ? Field("Repository", repository)
            : Field("Pull request", string.IsNullOrWhiteSpace(repository) ? numberLabel : $"{repository} {numberLabel}");
        return new(title ?? $"Pull request {numberLabel}",
            lifecycle?.Text,
            lifecycle is { IsDanger: true } ? "danger" : null,
            Present(reference, Note(checks), context), action);
    }

    public static string? Join(string? label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : string.IsNullOrWhiteSpace(label) ? value : $"{label} · {value}";
}
