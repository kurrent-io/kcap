using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// A session owner's round avatar on a row. The row names it for assistive tech; the initials are never read.
public partial class OwnerAvatar : UserControl {
    public static readonly StyledProperty<string?> InitialsProperty = AvaloniaProperty.Register<OwnerAvatar, string?>(nameof(Initials));
    public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<OwnerAvatar, string?>(nameof(Url));

    public string? Initials { get => GetValue(InitialsProperty); set => SetValue(InitialsProperty, value); }
    public string? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }

    public OwnerAvatar() => InitializeComponent();
}
