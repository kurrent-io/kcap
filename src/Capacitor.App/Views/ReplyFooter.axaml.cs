using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// A reply's time and Copy, shown while the pointer or keyboard focus is on any of its rows.
public partial class ReplyFooter : UserControl {
    public static readonly StyledProperty<string?> MetadataProperty =
        AvaloniaProperty.Register<ReplyFooter, string?>(nameof(Metadata));

    public ReplyFooter() => InitializeComponent();

    /// The sender and full time, for the time's tooltip and what assistive technology reads.
    public string? Metadata {
        get => GetValue(MetadataProperty);
        set => SetValue(MetadataProperty, value);
    }
}
