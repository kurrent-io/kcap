using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lucide.Avalonia;

namespace Capacitor.App.Views.Rail;

/// One entry of the rail's top list: its icon in the column the session and work item rows put theirs, its label,
/// and its shortcut, which the rail shows only on hover or focus. Read aloud by its label unless named otherwise.
public sealed class RailNavRow : Button {
    public static readonly StyledProperty<LucideIconKind> IconProperty =
        AvaloniaProperty.Register<RailNavRow, LucideIconKind>(nameof(Icon));
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<RailNavRow, string>(nameof(Label), "");
    public static readonly StyledProperty<string?> ShortcutProperty =
        AvaloniaProperty.Register<RailNavRow, string?>(nameof(Shortcut));

    public LucideIconKind Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Shortcut { get => GetValue(ShortcutProperty); set => SetValue(ShortcutProperty, value); }

    protected override Type StyleKeyOverride => typeof(Button);

    public RailNavRow() {
        // Hover and selection come from the rail's row styles.
        Classes.Add("railRow");
        Classes.Add("railNav");
        this[!AutomationProperties.NameProperty] = this[!LabelProperty];
        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("navLabel");
        label[!TextBlock.TextProperty] = this[!LabelProperty];
        var shortcut = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        shortcut.Classes.Add("navShortcut");
        shortcut[!TextBlock.TextProperty] = this[!ShortcutProperty];
        Grid.SetColumn(label, 1);
        Grid.SetColumn(shortcut, 2);
        Content = new Grid {
            ColumnDefinitions = new("30,*,Auto"),
            Children = { new AppIcon { [!LucideIcon.KindProperty] = this[!IconProperty] }, label, shortcut },
        };
    }
}
