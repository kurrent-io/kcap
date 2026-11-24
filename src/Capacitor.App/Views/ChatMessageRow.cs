using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public sealed class ChatMessageRow : Panel {
    public static readonly StyledProperty<bool> InlineChromeProperty =
        AvaloniaProperty.Register<ChatMessageRow, bool>(nameof(InlineChrome));

    public static readonly StyledProperty<string?> AuthorProperty =
        AvaloniaProperty.Register<ChatMessageRow, string?>(nameof(Author));

    public static readonly StyledProperty<ChatTimestamp?> TimestampProperty =
        AvaloniaProperty.Register<ChatMessageRow, ChatTimestamp?>(nameof(Timestamp));

    public static readonly DirectProperty<ChatMessageRow, string?> AccessibleMetadataProperty =
        AvaloniaProperty.RegisterDirect<ChatMessageRow, string?>(nameof(AccessibleMetadata), row => row.AccessibleMetadata);

    FlyoutBase? _openMenu;
    Visual? _messageBody;
    ChatMessageHeader? _header;
    bool _pointerOnMessage;

    public bool InlineChrome {
        get => GetValue(InlineChromeProperty);
        set => SetValue(InlineChromeProperty, value);
    }

    public string? Author {
        get => GetValue(AuthorProperty);
        set => SetValue(AuthorProperty, value);
    }

    public ChatTimestamp? Timestamp {
        get => GetValue(TimestampProperty);
        set => SetValue(TimestampProperty, value);
    }

    public string? AccessibleMetadata {
        get;
        private set => SetAndRaise(AccessibleMetadataProperty, ref field, value);
    }

    internal MessageCopyButton? CopyAction => this.GetVisualDescendants().OfType<MessageCopyButton>()
        .FirstOrDefault(copy => copy is { IsEffectivelyVisible: true, Text.Length: > 0 });

    internal void SetOpenMenu(FlyoutBase? menu) {
        _openMenu = menu;
        UpdateReveal();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        if (!InlineChrome || e.Handled || e.Key != Key.F10 || e.KeyModifiers != KeyModifiers.Shift
            || e.Source is not InputElement source) return;
        var request = new ContextRequestedEventArgs { RoutedEvent = ContextRequestedEvent };
        source.RaiseEvent(request);
        e.Handled = request.Handled;
    }

    protected override void OnDataContextChanged(EventArgs e) {
        _openMenu?.Hide();
        SetPointerOnMessage(false);
        base.OnDataContextChanged(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _openMenu?.Hide();
        SetPointerOnMessage(false);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);
        SetPointerOnMessage(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == InlineChromeProperty) {
            if (InlineChrome) {
                AddHandler(PointerMovedEvent, OnMessagePointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
            } else {
                RemoveHandler(PointerMovedEvent, OnMessagePointerMoved);
                _openMenu?.Hide();
                SetPointerOnMessage(false);
                _messageBody = null;
                _header = null;
            }
            PseudoClasses.Set(":inline-chrome", InlineChrome);
        }
        if (change.Property == InlineChromeProperty || change.Property == IsKeyboardFocusWithinProperty) UpdateReveal();
        if (change.Property == InlineChromeProperty || change.Property == AuthorProperty || change.Property == TimestampProperty)
            AccessibleMetadata = DescribeMessage();
    }

    string? DescribeMessage() {
        if (!InlineChrome) return null;
        return Timestamp is { } stamp ? $"{Author} · {stamp.Tip}" : Author;
    }

    void OnMessagePointerMoved(object? sender, PointerEventArgs e) {
        if (e.Pointer.Type == PointerType.Touch) return;
        _messageBody ??= Classes.Contains("userRow")
            ? this.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("userMessageBody"))
            : this.GetVisualDescendants().OfType<MarkdownView>().FirstOrDefault();
        _header ??= this.GetVisualDescendants().OfType<ChatMessageHeader>().FirstOrDefault();
        if (_messageBody?.TranslatePoint(default, this) is not { } bodyOrigin
            || _header?.TranslatePoint(default, this) is not { } headerOrigin) return;

        var point = e.GetPosition(this);
        var body = new Rect(bodyOrigin, _messageBody.Bounds.Size);
        var header = new Rect(headerOrigin, _header.Bounds.Size);
        // The gap stays traversable while the actions are revealed.
        var bridge = new Rect(Math.Min(body.X, header.X), header.Y,
            Math.Max(body.Right, header.Right) - Math.Min(body.X, header.X), header.Height);
        SetPointerOnMessage(body.Contains(point) || (ShowsDetails && bridge.Contains(point)));
    }

    void SetPointerOnMessage(bool value) {
        if (_pointerOnMessage == value) return;
        _pointerOnMessage = value;
        UpdateReveal();
    }

    bool ShowsDetails => InlineChrome && (_pointerOnMessage || IsKeyboardFocusWithin || _openMenu is not null);

    void UpdateReveal() =>
        PseudoClasses.Set(":show-details", ShowsDetails);
}
