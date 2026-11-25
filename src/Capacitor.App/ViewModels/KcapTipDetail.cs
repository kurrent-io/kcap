namespace Capacitor.App.ViewModels;

public sealed record KcapTipDetail(string Value, string? Label = null) {
    public bool HasLabel => Label is not null;
    internal string Description => HasLabel ? $"{Label} · {Value}" : Value;

    internal static KcapTipDetail? Note(string? value) => Field(null, value);

    internal static KcapTipDetail? Field(string? label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new(value, string.IsNullOrWhiteSpace(label) ? null : label);

    internal static KcapTipDetail[] Present(params KcapTipDetail?[] rows) =>
        rows.OfType<KcapTipDetail>().ToArray();
}
