using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Capacitor.App.ViewModels.Inbox;

namespace Capacitor.App.Views.Inbox;

/// <summary>A notification's styled runs as one wrapping paragraph. A linked run is a link button inline with the
/// text, so a long headline still wraps word by word.</summary>
public sealed class InboxRunsText : TextBlock {
    public static readonly StyledProperty<IReadOnlyList<InboxRun>?> RunsProperty =
        AvaloniaProperty.Register<InboxRunsText, IReadOnlyList<InboxRun>?>(nameof(Runs));

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<InboxRunsText, ICommand?>(nameof(OpenCommand));

    public IReadOnlyList<InboxRun>? Runs {
        get => GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    public ICommand? OpenCommand {
        get => GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public InboxRunsText() => TextWrapping = TextWrapping.Wrap;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == RunsProperty || change.Property == OpenCommandProperty) Rebuild();
    }

    void Rebuild() {
        var inlines = new InlineCollection();
        foreach (var run in Runs ?? []) inlines.Add(run.Url is { } url ? Link(run, url) : Span(run));
        Inlines = inlines;
    }

    static Inline Span(InboxRun run) {
        var span = new Run(run.Text);
        if (run.Style == InboxRunStyle.Strong) span.FontWeight = FontWeight.SemiBold;
        if (run.Style == InboxRunStyle.Code && Application.Current?.FindResource("KcapFontFamilyMono") is FontFamily mono) span.FontFamily = mono;
        return span;
    }

    Inline Link(InboxRun run, string url) {
        var text = new TextBlock { Text = run.Text, TextWrapping = TextWrapping.Wrap, FontWeight = run.Style == InboxRunStyle.Strong ? FontWeight.SemiBold : FontWeight.Normal };
        var button = new Button {
            Content = text, Command = OpenCommand, CommandParameter = url, VerticalAlignment = VerticalAlignment.Bottom,
        };
        button.Classes.Add("kcapLink");
        ToolTip.SetTip(button, url);
        return new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.TextBottom };
    }
}
