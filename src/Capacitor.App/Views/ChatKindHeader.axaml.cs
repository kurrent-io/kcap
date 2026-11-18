using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

public partial class ChatKindHeader : UserControl {
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ChatKindHeader, string>(nameof(Label), "");

    public static readonly StyledProperty<LucideIconKind?> IconKindProperty =
        AvaloniaProperty.Register<ChatKindHeader, LucideIconKind?>(nameof(IconKind));

    public static readonly StyledProperty<object?> StatusProperty =
        AvaloniaProperty.Register<ChatKindHeader, object?>(nameof(Status));

    public static readonly StyledProperty<IDataTemplate?> StatusTemplateProperty =
        AvaloniaProperty.Register<ChatKindHeader, IDataTemplate?>(nameof(StatusTemplate));

    public ChatKindHeader() => InitializeComponent();

    public string Label {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public LucideIconKind? IconKind {
        get => GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public object? Status {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public IDataTemplate? StatusTemplate {
        get => GetValue(StatusTemplateProperty);
        set => SetValue(StatusTemplateProperty, value);
    }
}
