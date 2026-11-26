using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// A prompt or assistant message whose time and Copy appear while the pointer or keyboard is on it.
public sealed class ChatMessageRow : Panel {
    public static readonly StyledProperty<string?> AuthorProperty =
        AvaloniaProperty.Register<ChatMessageRow, string?>(nameof(Author));

    public static readonly StyledProperty<ChatTimestamp?> TimestampProperty =
        AvaloniaProperty.Register<ChatMessageRow, ChatTimestamp?>(nameof(Timestamp));

    public static readonly DirectProperty<ChatMessageRow, string?> AccessibleMetadataProperty =
        AvaloniaProperty.RegisterDirect<ChatMessageRow, string?>(nameof(AccessibleMetadata), row => row.AccessibleMetadata);

    FlyoutBase? _openMenu;
    Visual? _messageBody;
    ChatMessageHeader? _header;
    StackPanel? _footer;
    bool _pointerOnMessage;
    readonly KeyboardReveal _keyboard;

    public ChatMessageRow() {
        _keyboard = new KeyboardReveal(this, UpdateReveal);
        AddHandler(PointerMovedEvent, OnMessagePointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
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

    /// This message alone, where the reply's Copy takes every message in it.
    internal (string Text, MarkdownFlavor Flavor)? MessageText => DataContext switch {
        UserTurnItem { HasText: true } prompt => (prompt.Text, MarkdownFlavor.Prompt),
        AssistantTextItem { Text.Length: > 0 } reply => (reply.Text, MarkdownFlavor.Chat),
        _ => null,
    };

    internal async Task CopyMessageAsync() {
        if (MessageText is not var (text, flavor)) return;
        string receipt;
        try {
            receipt = await MarkdownClipboard.CopyAsync(this, text, flavor) ? "Copied" : "Copy failed";
        } catch (Exception) {
            receipt = "Copy failed";
        }
        LiveAnnouncer.Polite(this, receipt);
    }

    // Focus spends the menu's lifetime inside it; on closing it returns here or goes on elsewhere.
    internal void SetOpenMenu(FlyoutBase? menu) {
        _openMenu = menu;
        if (menu is null && !IsKeyboardFocusWithin) _keyboard.FocusLeft();
        UpdateReveal();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.F10 || e.KeyModifiers != KeyModifiers.Shift || e.Source is not InputElement source) return;
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
        if (change.Property == IsKeyboardFocusWithinProperty) {
            if (!IsKeyboardFocusWithin && _openMenu is null) _keyboard.FocusLeft();
            UpdateReveal();
        }
        if (change.Property == AuthorProperty || change.Property == TimestampProperty)
            AccessibleMetadata = Timestamp is { } stamp ? $"{Author} · {stamp.Tip}" : Author;
    }

    void OnMessagePointerMoved(object? sender, PointerEventArgs e) {
        if (e.Pointer.Type == PointerType.Touch) return;
        _messageBody ??= Classes.Contains("userRow")
            ? this.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("userMessageBody"))
            : this.GetVisualDescendants().OfType<MarkdownView>().FirstOrDefault();
        _header ??= this.GetVisualDescendants().OfType<ChatMessageHeader>().FirstOrDefault();
        _footer ??= this.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(panel => panel.Classes.Contains("messageFooter"));
        if (_messageBody?.TranslatePoint(default, this) is not { } bodyOrigin) return;

        var point = e.GetPosition(this);
        var body = new Rect(bodyOrigin, _messageBody.Bounds.Size);
        // Once revealed, the actions stay while the pointer crosses from the body onto them.
        var message = body;
        foreach (var part in new Visual?[] { _header, _footer }) {
            if (part is { IsEffectivelyVisible: true } && part.TranslatePoint(default, this) is { } origin)
                message = message.Union(new Rect(origin, part.Bounds.Size));
        }
        SetPointerOnMessage(body.Contains(point) || (ShowsDetails && message.Contains(point)));
    }

    void SetPointerOnMessage(bool value) {
        if (_pointerOnMessage == value) return;
        _pointerOnMessage = value;
        UpdateReveal();
    }

    bool ShowsDetails => _pointerOnMessage || _keyboard.IsOn || _openMenu is not null;

    void UpdateReveal() =>
        PseudoClasses.Set(":show-details", ShowsDetails);
}
