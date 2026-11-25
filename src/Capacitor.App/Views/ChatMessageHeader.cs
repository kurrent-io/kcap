using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// A message's marks, beside its first line, and the name of another participant who sent it.
public sealed class ChatMessageHeader : StackPanel {
    public static readonly StyledProperty<bool> ShowParticipantProperty =
        AvaloniaProperty.Register<ChatMessageHeader, bool>(nameof(ShowParticipant));

    public bool ShowParticipant {
        get => GetValue(ShowParticipantProperty);
        set => SetValue(ShowParticipantProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == ShowParticipantProperty) PseudoClasses.Set(":participant", ShowParticipant);
    }
}
