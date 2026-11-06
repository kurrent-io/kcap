using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

public partial class SessionConnectionNotice : UserControl {
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<SessionConnectionNotice, bool>(nameof(IsCompact));
    public bool IsCompact {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }
    public SessionConnectionNotice() => InitializeComponent();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == IsCompactProperty) Classes.Set("compact", IsCompact);
    }
}
