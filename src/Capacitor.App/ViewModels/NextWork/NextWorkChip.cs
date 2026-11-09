namespace Capacitor.App.ViewModels.NextWork;

public sealed record NextWorkChip(string Text, NextWorkTone Tone) {
    public bool IsWarning => Tone == NextWorkTone.Warning;
    public bool IsDanger  => Tone == NextWorkTone.Danger;
    public bool IsSuccess => Tone == NextWorkTone.Success;
}
