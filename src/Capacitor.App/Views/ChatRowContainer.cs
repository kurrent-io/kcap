using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Holds one transcript row and, when the row ends an agent reply, the reply's time and Copy under it.
/// The footer goes inside the row's own bottom gap, so it sits against the row and the gap follows it.
public sealed class ChatRowContainer : Panel {
    const double FooterGap = 4;
    readonly ContentPresenter _content = new();
    readonly ReplyFooter _footer = new() { HorizontalAlignment = HorizontalAlignment.Left };
    readonly KeyboardReveal _keyboard;
    ChatItemViewModel? _item;
    AssistantReply? _active;
    bool _pointerInside;

    public ChatRowContainer() {
        Background = Brushes.Transparent;
        Children.Add(_content);
        Children.Add(_footer);
        _keyboard = new KeyboardReveal(this, UpdateActive);
    }

    /// The row's own control, as its template built it.
    public Control? Row => _content.Child;

    internal ReplyFooter Footer => _footer;

    internal void Show(object? item) {
        if (_item is not null) _item.PropertyChanged -= OnItemChanged;
        _item = item as ChatItemViewModel;
        if (_item is not null) _item.PropertyChanged += OnItemChanged;
        DataContext = item;
        _content.Content = item;
        _footer.IsVisible = _item?.EndsReply == true;
        UpdateActive();
    }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(ChatItemViewModel.EndsReply)) {
            _footer.IsVisible = _item?.EndsReply == true;
            InvalidateMeasure();
        } else if (e.PropertyName == nameof(ChatItemViewModel.Reply)) UpdateActive();
    }

    protected override void OnPointerEntered(PointerEventArgs e) {
        base.OnPointerEntered(e);
        _pointerInside = e.Pointer.Type != PointerType.Touch;
        UpdateActive();
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        _pointerInside = false;
        UpdateActive();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty && !IsKeyboardFocusWithin) _keyboard.FocusLeft();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _pointerInside = false;
        _keyboard.FocusLeft();
        UpdateActive();
        base.OnDetachedFromVisualTree(e);
    }

    // Every row of a reply counts toward it, so its time and Copy show from any of them.
    void UpdateActive() {
        var reply = _pointerInside || _keyboard.IsOn ? _item?.Reply : null;
        if (ReferenceEquals(reply, _active)) return;
        _active?.Leave();
        _active = reply;
        _active?.Enter();
    }

    // A card row keeps its gap on the card it hosts rather than on itself.
    double RowGap => _content.Child switch {
        ContentControl { Content: Layoutable card } host => host.Margin.Bottom + card.Margin.Bottom,
        Layoutable row => row.Margin.Bottom,
        _ => 0,
    };

    protected override Size MeasureOverride(Size availableSize) {
        _content.Measure(availableSize);
        var size = _content.DesiredSize;
        if (!_footer.IsVisible) return size;
        _footer.Measure(availableSize);
        return new Size(Math.Max(size.Width, _footer.DesiredSize.Width), size.Height + FooterGap + _footer.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        var footer = _footer.IsVisible ? FooterGap + _footer.DesiredSize.Height : 0;
        var content = Math.Max(0, finalSize.Height - footer);
        _content.Arrange(new Rect(0, 0, finalSize.Width, content));
        if (_footer.IsVisible)
            _footer.Arrange(new Rect(0, content - RowGap + FooterGap, _footer.DesiredSize.Width, _footer.DesiredSize.Height));
        return finalSize;
    }
}
