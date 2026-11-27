using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// A message's bookmark and topic toggles, in its time and Copy row. The bookmark sits nearer Copy,
/// at the row's outer edge.
public partial class MessageMarks : UserControl {
    public static readonly StyledProperty<bool> ReadsFromRightProperty =
        AvaloniaProperty.Register<MessageMarks, bool>(nameof(ReadsFromRight));

    public MessageMarks() => InitializeComponent();

    /// Whether the row's outer edge is on the right, as a prompt's is, rather than the left, as a reply's is.
    public bool ReadsFromRight {
        get => GetValue(ReadsFromRightProperty);
        set => SetValue(ReadsFromRightProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property != ReadsFromRightProperty) return;
        Toggles.Children.Remove(BookmarkButton);
        Toggles.Children.Insert(ReadsFromRight ? 1 : 0, BookmarkButton);
    }

    // The topic's header lands above its message, so the chat holds the toggle under the pointer.
    void OnTopicClick(object? sender, RoutedEventArgs e) => this.FindAncestorOfType<ChatTabView>()?.KeepToggleInPlace(sender);
}
