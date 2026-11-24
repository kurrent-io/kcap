using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

public sealed class ChatMessageHeader : StackPanel {
    public static readonly StyledProperty<bool> BesideMessageProperty =
        AvaloniaProperty.Register<ChatMessageHeader, bool>(nameof(BesideMessage));

    public static readonly StyledProperty<bool> ShowParticipantProperty =
        AvaloniaProperty.Register<ChatMessageHeader, bool>(nameof(ShowParticipant));

    static ChatMessageHeader() {
        AffectsMeasure<ChatMessageHeader>(BesideMessageProperty, ShowParticipantProperty);
    }

    public bool BesideMessage {
        get => GetValue(BesideMessageProperty);
        set => SetValue(BesideMessageProperty, value);
    }

    public bool ShowParticipant {
        get => GetValue(ShowParticipantProperty);
        set => SetValue(ShowParticipantProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == ShowParticipantProperty) PseudoClasses.Set(":participant", ShowParticipant);
    }

    const int AuthorIndex = 0;
    const int TimeIndex = 1;
    const int CopyIndex = 2;
    const double ActionGap = 4;

    double LabelWidth => Math.Max(Children[AuthorIndex].DesiredSize.Width, Children[TimeIndex].DesiredSize.Width);
    double LabelHeight => ShowParticipant
        ? Children[AuthorIndex].DesiredSize.Height + Children[TimeIndex].DesiredSize.Height
        : Children[TimeIndex].DesiredSize.Height;

    protected override Size MeasureOverride(Size availableSize) {
        if (!BesideMessage) return base.MeasureOverride(availableSize);

        var height = 0d;
        foreach (var child in Children) {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        var width = LabelWidth;
        for (var i = CopyIndex; i < Children.Count; i++) {
            if (Children[i].IsVisible) width += ActionGap + Children[i].DesiredSize.Width;
        }
        return new Size(width, Math.Max(height, LabelHeight));
    }

    protected override Size ArrangeOverride(Size finalSize) {
        if (!BesideMessage) return base.ArrangeOverride(finalSize);

        var labelWidth = LabelWidth;
        var x = labelWidth;
        for (var i = CopyIndex; i < Children.Count; i++) {
            var action = Children[i];
            if (!action.IsVisible) continue;
            x += ActionGap;
            action.Arrange(new Rect(x, 0, action.DesiredSize.Width, finalSize.Height));
            x += action.DesiredSize.Width;
        }

        var authorHeight = ShowParticipant ? Children[AuthorIndex].DesiredSize.Height : 0;
        var labelTop = (finalSize.Height - LabelHeight) / 2;
        Children[AuthorIndex].Arrange(ShowParticipant
            ? new Rect(0, labelTop, labelWidth, authorHeight)
            : new Rect(0, 0, labelWidth, finalSize.Height));
        Children[TimeIndex].Arrange(new Rect(0, labelTop + authorHeight, labelWidth, Children[TimeIndex].DesiredSize.Height));
        return finalSize;
    }
}
