using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// Copies a whole chat message with its formatting, and confirms the copy in place.
public partial class MessageCopyButton : UserControl {
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<MessageCopyButton, string?>(nameof(Text));

    public static readonly StyledProperty<MarkdownFlavor> FlavorProperty =
        AvaloniaProperty.Register<MessageCopyButton, MarkdownFlavor>(nameof(Flavor));

    static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);
    int _copies;

    public MessageCopyButton() => InitializeComponent();

    public string? Text {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public MarkdownFlavor Flavor {
        get => GetValue(FlavorProperty);
        set => SetValue(FlavorProperty, value);
    }

    internal Task? PendingCopyForTesting { get; private set; }

    void OnCopyClick(object? sender, RoutedEventArgs e) => PendingCopyForTesting = CopyAsync();

    async Task CopyAsync() {
        if (Text is not { Length: > 0 } text) return;
        var mine = ++_copies;
        try {
            Show(await MarkdownClipboard.CopyAsync(this, text, Flavor) ? "Copied" : "Copy failed");
        } catch (Exception) {
            Show("Copy failed");
        }
        // A later copy's receipt outranks this one's expiry rather than being cut short by it.
        DispatcherTimer.RunOnce(() => { if (_copies == mine) Show("Copy message"); }, CopiedFor);
    }

    void Show(string tip) {
        var copied = tip == "Copied";
        CopyIcon.IsVisible = !copied;
        CopiedIcon.IsVisible = copied;
        ToolTip.SetTip(CopyButton, tip);
        ToolTip.SetIsOpen(CopyButton, tip != "Copy message");
    }
}
