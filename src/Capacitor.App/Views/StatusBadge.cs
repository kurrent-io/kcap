using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

public sealed class StatusBadge : Border {
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<StatusBadge, string?>(nameof(Text));
    public static readonly StyledProperty<LucideIconKind?> IconProperty = AvaloniaProperty.Register<StatusBadge, LucideIconKind?>(nameof(Icon));
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = AvaloniaProperty.Register<StatusBadge, FontFamily>(nameof(FontFamily), FontFamily.Default);

    readonly AppIcon _icon = new() { Size = 12, Margin = new Thickness(0, 0, 5, 0), IsVisible = false };

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public LucideIconKind? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public StatusBadge() {
        Classes.Add("kcapBadge");
        var word = new TextBlock { Classes = { "kcapStatusWord" }, TextTrimming = TextTrimming.CharacterEllipsis };
        word.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        word.Bind(TextBlock.FontFamilyProperty, this.GetObservable(FontFamilyProperty));
        this.Bind(AutomationProperties.NameProperty, this.GetObservable(TextProperty), Avalonia.Data.BindingPriority.Style);
        this.Bind(ToolTip.TipProperty, this.GetObservable(TextProperty), Avalonia.Data.BindingPriority.Style);
        var content = new DockPanel();
        DockPanel.SetDock(_icon, Dock.Left);
        content.Children.Add(_icon);
        content.Children.Add(word);
        Child = content;
    }

    protected override Type StyleKeyOverride => typeof(Border);
    protected override AutomationPeer OnCreateAutomationPeer() => new StatusBadgeAutomationPeer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property != IconProperty) return;
        _icon.IsVisible = Icon is not null;
        if (Icon is { } kind) _icon.Kind = kind;
    }
}
