using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    readonly KeyboardReveal _keyboard;
    ChatItemViewModel? _item;
    AssistantReply? _active;
    // Focus spends a menu's lifetime inside it; on closing it returns here or goes on elsewhere.
    FlyoutBase? _footerMenu;
    bool _pointerInside;

    public ChatRowContainer() {
        Background = Brushes.Transparent;
        Children.Add(Footer);
        _keyboard = new KeyboardReveal(this, UpdateActive);
    }

    /// The row's own control, as its template built it.
    public Control? Row { get; private set; }

    /// The template that built <see cref="Row"/>.
    internal IDataTemplate? Template { get; private set; }

    internal ReplyFooter Footer { get; } = new() { HorizontalAlignment = HorizontalAlignment.Left };

    internal void Show(object? item, IDataTemplate? template) {
        if (_item is not null) _item.PropertyChanged -= OnItemChanged;
        _item = item as ChatItemViewModel;
        if (_item is not null) _item.PropertyChanged += OnItemChanged;
        if (item is not null && (Row is null || !ReferenceEquals(template, Template))) {
            if (Row is not null) Children.Remove(Row);
            Template = template;
            Row = template?.Build(item);
            if (Row is not null) Children.Insert(0, Row);
        }
        DataContext = item;
        Footer.IsVisible = _item?.EndsReply == true;
        UpdateActive();
    }

    /// Lets go of the item, and of what the reader did in this row, so the row can serve another.
    internal void Park() {
        var view = this.FindAncestorOfType<ChatTabView>();
        if (Row is not null)
            foreach (var markdown in Row.GetSelfAndVisualDescendants().OfType<MarkdownView>()) {
                view?.ReleaseRecycledMarkdown(markdown);
                markdown.ResetMessageState();
            }
        Show(null, Template);
    }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(ChatItemViewModel.EndsReply):
                Footer.IsVisible = _item?.EndsReply == true;
                InvalidateMeasure();
                break;
            case nameof(ChatItemViewModel.Reply):
                UpdateActive();
                break;
        }
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
            || e.Source is not Visual source || !Footer.IsVisualAncestorOf(source)
            || ReplyMessageRow() is not { ContextFlyout: { } menu } row) return;
        _footerMenu = menu;
        menu.Closed += OnFooterMenuClosed;
        menu.ShowAt(row);
        e.Handled = true;
    }

    void OnFooterMenuClosed(object? sender, EventArgs e) {
        if (sender is FlyoutBase menu) menu.Closed -= OnFooterMenuClosed;
        _footerMenu = null;
        if (!IsKeyboardFocusWithin) _keyboard.FocusLeft();
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
        if (change.Property == IsKeyboardFocusWithinProperty && !IsKeyboardFocusWithin && _footerMenu is null) _keyboard.FocusLeft();
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
    double RowGap => Row switch {
        ContentControl { Content: Layoutable card } host => host.Margin.Bottom + card.Margin.Bottom,
        Layoutable row => row.Margin.Bottom,
        _ => 0,
    };

    protected override Size MeasureOverride(Size availableSize) {
        Row?.Measure(availableSize);
        var size = Row?.DesiredSize ?? default;
        if (!Footer.IsVisible) return size;
        Footer.Measure(availableSize);
        return new Size(Math.Max(size.Width, Footer.DesiredSize.Width), size.Height + FooterGap + Footer.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        var footer = Footer.IsVisible ? FooterGap + Footer.DesiredSize.Height : 0;
        var content = Math.Max(0, finalSize.Height - footer);
        Row?.Arrange(new Rect(0, 0, finalSize.Width, content));
        if (Footer.IsVisible)
            Footer.Arrange(new Rect(0, content - RowGap + FooterGap, Footer.DesiredSize.Width, Footer.DesiredSize.Height));
        return finalSize;
    }
}
