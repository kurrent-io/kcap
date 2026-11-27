using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public sealed class StructuredTooltip : AvaloniaObject, IDisposable {
    public static readonly AttachedProperty<KcapTip?> ModelProperty =
        AvaloniaProperty.RegisterAttached<StructuredTooltip, Control, KcapTip?>("Model");
    static readonly AttachedProperty<StructuredTooltip?> ActiveProperty =
        AvaloniaProperty.RegisterAttached<StructuredTooltip, Control, StructuredTooltip?>("Active");
    static readonly IDataTemplate Template = new FuncDataTemplate<KcapTip>((model, _) => new KcapTooltip { DataContext = model });

    public static IMultiValueConverter Session { get; } = new FuncMultiValueConverter<object?, KcapTip?>(values => {
        var fields = values.ToArray();
        return fields is [AgentStatusPresentation status, var title, var machine, var checkout, var laneNote]
            ? KcapTip.Session(status, title as string, machine as string, checkout as string, laneNote as string) : null;
    });

    readonly Control _target;
    readonly ToolTip _tip;
    readonly IDisposable? _tipValue;
    IDisposable? _description;
    IDisposable? _keyboardPlacement;

    static StructuredTooltip() => ModelProperty.Changed.AddClassHandler<Control>((target, _) => Update(target));

    public static KcapTip? GetModel(Control control) => control.GetValue(ModelProperty);
    public static void SetModel(Control control, KcapTip? value) => control.SetValue(ModelProperty, value);

    static void Update(Control target) {
        var active = target.GetValue(ActiveProperty);
        if (GetModel(target) is { } model) {
            if (active is null) target.SetValue(ActiveProperty, new StructuredTooltip(target, model));
            else active.Apply(model);
        } else if (active is not null) {
            active.Dispose();
            target.ClearValue(ActiveProperty);
        }
    }

    StructuredTooltip(Control target, KcapTip model) {
        _target = target;
        _tip = new ToolTip { ContentTemplate = Template };
        Apply(model);
        ToolTip.SetIsOpen(target, false);
        _tipValue = target.SetValue(ToolTip.TipProperty, _tip, BindingPriority.Animation);
        target.KeyDown += OnKeyDown;
        target.LostFocus += OnLostFocus;
        target.PropertyChanged += OnTargetPropertyChanged;
    }

    void Apply(KcapTip model) {
        _tip.Content = model;
        _description?.Dispose();
        _description = _target.SetValue(AutomationProperties.HelpTextProperty, model.Description, BindingPriority.Animation);
    }

    void OnKeyDown(object? sender, KeyEventArgs e) {
        if (!_target.IsFocused) return;
        if (e.Key == Key.F1 && e.KeyModifiers == KeyModifiers.Shift) {
            ToolTip.SetIsOpen(_target, false);
            _keyboardPlacement = _target.SetValue(ToolTip.PlacementProperty, PlacementMode.Bottom, BindingPriority.Animation);
            ToolTip.SetIsOpen(_target, true);
            if (!ToolTip.GetIsOpen(_target)) RestorePlacement();
            e.Handled = true;
        } else if (e.Key == Key.Escape && ToolTip.GetIsOpen(_target)) {
            ToolTip.SetIsOpen(_target, false);
            e.Handled = true;
        }
    }

    void OnLostFocus(object? sender, RoutedEventArgs e) {
        if (_keyboardPlacement is not null) ToolTip.SetIsOpen(_target, false);
    }

    void OnTargetPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e) {
        if (e.Property == ToolTip.IsOpenProperty && !ToolTip.GetIsOpen(_target)) RestorePlacement();
    }

    void RestorePlacement() {
        _keyboardPlacement?.Dispose();
        _keyboardPlacement = null;
    }

    public void Dispose() {
        ToolTip.SetIsOpen(_target, false);
        RestorePlacement();
        _target.PropertyChanged -= OnTargetPropertyChanged;
        _target.LostFocus -= OnLostFocus;
        _target.KeyDown -= OnKeyDown;
        _tipValue?.Dispose();
        _tip.Content = null;
        _description?.Dispose();
    }
}
