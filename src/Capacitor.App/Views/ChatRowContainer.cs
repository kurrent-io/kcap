using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Holds one transcript row and, when the row ends an agent reply, the reply's time and Copy under it.
/// The footer goes inside the row's own bottom gap, so it sits against the row and the gap follows it.
/// The row its template built stays for the next item with the same template, so recycling only rebinds it.
public sealed class ChatRowContainer : Panel {
    const double FooterGap = 4;
    readonly ReplyFooter _footer = new() { HorizontalAlignment = HorizontalAlignment.Left };
    Control? _row;
    readonly KeyboardReveal _keyboard;
    ChatItemViewModel? _item;
    AssistantReply? _active;
    bool _pointerInside;

    public ChatRowContainer() {
        Background = Brushes.Transparent;
        Children.Add(_footer);
        _keyboard = new KeyboardReveal(this, UpdateActive);
    }

    /// The row's own control, as its template built it.
    public Control? Row => _row;

    /// The template that built <see cref="Row"/>.
    internal IDataTemplate? Template { get; private set; }

    internal ReplyFooter Footer => _footer;

    internal void Show(object? item, IDataTemplate? template) {
        if (_item is not null) _item.PropertyChanged -= OnItemChanged;
        _item = item as ChatItemViewModel;
        if (_item is not null) _item.PropertyChanged += OnItemChanged;
        if (item is not null && (_row is null || !ReferenceEquals(template, Template))) {
            if (_row is not null) Children.Remove(_row);
            Template = template;
            _row = template?.Build(item);
            if (_row is not null) Children.Insert(0, _row);
        }
        DataContext = item;
        _footer.IsVisible = _item?.EndsReply == true;
        UpdateActive();
    }

    /// Lets go of the item, and of what the reader did in this row, so the row can serve another.
    internal void Park() {
        var view = this.FindAncestorOfType<ChatTabView>();
        if (_row is not null)
            foreach (var markdown in _row.GetSelfAndVisualDescendants().OfType<MarkdownView>()) {
                view?.ReleaseRecycledMarkdown(markdown);
                markdown.ResetMessageState();
            }
        Show(null, Template);
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

    // The footer sits beside the message rows, so Shift+F10 on it opens the menu of the reply's last message.
    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.F10 || e.KeyModifiers != KeyModifiers.Shift
            || e.Source is not Visual source || !_footer.IsVisualAncestorOf(source)
            || ReplyMessageRow() is not { ContextFlyout: { } menu } row) return;
        menu.ShowAt(row);
        e.Handled = true;
    }

    ChatMessageRow? ReplyMessageRow() {
        if (_item?.Reply is not { } reply || this.FindAncestorOfType<ItemsControl>() is not { } list) return null;
        var items = list.Items;
        for (var i = items.IndexOf(_item); i >= 0; i--) {
            if (items[i] is not AssistantTextItem message || !ReferenceEquals(message.Reply, reply)) continue;
            if (list.ContainerFromIndex(i) is null) list.ScrollIntoView(i);
            return list.ContainerFromIndex(i)?.GetVisualDescendants().OfType<ChatMessageRow>().FirstOrDefault();
        }
        return null;
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
    double RowGap => _row switch {
        ContentControl { Content: Layoutable card } host => host.Margin.Bottom + card.Margin.Bottom,
        Layoutable row => row.Margin.Bottom,
        _ => 0,
    };

    protected override Size MeasureOverride(Size availableSize) {
        _row?.Measure(availableSize);
        var size = _row?.DesiredSize ?? default;
        if (!_footer.IsVisible) return size;
        _footer.Measure(availableSize);
        return new Size(Math.Max(size.Width, _footer.DesiredSize.Width), size.Height + FooterGap + _footer.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        var footer = _footer.IsVisible ? FooterGap + _footer.DesiredSize.Height : 0;
        var content = Math.Max(0, finalSize.Height - footer);
        _row?.Arrange(new Rect(0, 0, finalSize.Width, content));
        if (_footer.IsVisible)
            _footer.Arrange(new Rect(0, content - RowGap + FooterGap, _footer.DesiredSize.Width, _footer.DesiredSize.Height));
        return finalSize;
    }
}
