namespace Capacitor.App.ViewModels;

public sealed record KcapTip(string Title, string? State = null, string? Tone = null,
        IReadOnlyList<string>? Details = null, string? Action = null) {
    public bool HasDetails => Details is { Count: > 0 };
    public bool IsDanger => Tone == "danger";
    public bool IsActive => Tone == "active";

    public string Description => string.Join("\n", new[] { Title, State }
        .Concat(Details ?? []).Append(Action).Where(line => !string.IsNullOrWhiteSpace(line)));

    public static KcapTip Session(AgentStatusPresentation status, string? title, string? machine, string? checkout) {
        var details = status.Facts
            .SelectMany(fact => Present(
                fact.Text == status.Label && !fact.HasCaption ? null : Join(fact.Caption, fact.Text), fact.Detail))
            .Concat(Present(Join("Remote daemon", machine), Join("Checkout", checkout)))
            .ToArray();
        var tone = status.IsDanger ? "danger" : status.IsInFlight ? "active" : null;
        return new(string.IsNullOrWhiteSpace(title) ? "Session" : title, status.Label, tone, details, "Open session");
    }

    public static KcapTip PullRequest(string identity, string? title, PullRequestStatus? lifecycle,
            string? repository, string? action, string? context = null, string? checks = null) =>
        new(string.IsNullOrWhiteSpace(title) ? identity : title,
            lifecycle?.Text,
            lifecycle is { IsDanger: true } ? "danger" : null,
            Present(string.IsNullOrWhiteSpace(title) ? repository : Join(repository, identity), checks, context), action);

    public static string? Join(string? label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : string.IsNullOrWhiteSpace(label) ? value : $"{label} · {value}";

    public static string[] Present(params string?[] lines) =>
        lines.OfType<string>().Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
}
