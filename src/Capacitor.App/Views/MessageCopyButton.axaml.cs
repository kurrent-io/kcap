using Avalonia;
using Avalonia.Automation;
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

    public static readonly StyledProperty<double> MinimumActionHeightProperty =
        AvaloniaProperty.Register<MessageCopyButton, double>(nameof(MinimumActionHeight));

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

    public double MinimumActionHeight {
        get => GetValue(MinimumActionHeightProperty);
        set => SetValue(MinimumActionHeightProperty, value);
    }

    internal Task? PendingCopyForTesting { get; private set; }

    void OnCopyClick(object? sender, RoutedEventArgs e) => PendingCopyForTesting = CopyAsync();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == MinimumActionHeightProperty)
            CopyButton.MinHeight = change.GetNewValue<double>();
        else if (change.Property == AutomationProperties.HelpTextProperty)
            AutomationProperties.SetHelpText(CopyButton, change.GetNewValue<string?>());
    }

    protected override void OnDataContextChanged(EventArgs e) {
        if (_copies != 0) {
            _copies++;
            Show("Copy message");
        }
        base.OnDataContextChanged(e);
    }

    async Task CopyAsync() {
        if (Text is not { Length: > 0 } text) return;
        var mine = ++_copies;
        string receipt;
        try {
            receipt = await MarkdownClipboard.CopyAsync(this, text, Flavor) ? "Copied" : "Copy failed";
        } catch (Exception) {
            receipt = "Copy failed";
        }
        // Writes can finish out of order; only the latest click shows a receipt or clears one.
        if (_copies != mine) return;
        Show(receipt);
        LiveAnnouncer.Polite(this, receipt);
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
