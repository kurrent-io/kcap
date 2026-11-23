using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Capacitor.App.ViewModels.Pages;
using Lucide.Avalonia;

namespace Capacitor.App.Views.Pages;

/// A list page filter: the pick on a field button, and a flyout that searches every choice, however many accumulate.
public sealed class FilterPicker : Button {
    public static readonly StyledProperty<IReadOnlyList<SessionsPageChoice>?> ChoicesProperty =
        AvaloniaProperty.Register<FilterPicker, IReadOnlyList<SessionsPageChoice>?>(nameof(Choices));
    public static readonly StyledProperty<SessionsPageChoice?> SelectedProperty =
        AvaloniaProperty.Register<FilterPicker, SessionsPageChoice?>(nameof(Selected), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string?> SearchPlaceholderProperty =
        AvaloniaProperty.Register<FilterPicker, string?>(nameof(SearchPlaceholder));

    public IReadOnlyList<SessionsPageChoice>? Choices { get => GetValue(ChoicesProperty); set => SetValue(ChoicesProperty, value); }
    public SessionsPageChoice? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public string? SearchPlaceholder { get => GetValue(SearchPlaceholderProperty); set => SetValue(SearchPlaceholderProperty, value); }

    protected override Type StyleKeyOverride => typeof(Button);

    readonly TextBlock _label = new() { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    public FilterPicker() {
        var chevron = new AppIcon { Kind = LucideIconKind.ChevronDown, Size = 14, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 1);
        Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _label, chevron } };
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedProperty) _label.Text = Selected?.Label;
    }

    protected override void OnClick() {
        base.OnClick();
        var search = new TextBox { Classes = { "kcapField" }, PlaceholderText = SearchPlaceholder, Margin = new Thickness(0, 0, 0, 6) };
        Avalonia.Automation.AutomationProperties.SetName(search, SearchPlaceholder ?? "Search");
        var rows = new ItemsControl { ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) };
        var root = new DockPanel { Width = Math.Max(Bounds.Width, 280) };
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);
        root.Children.Add(new ScrollViewer { MaxHeight = 320, Content = rows });
        var flyout = ChoiceMenus.PanelFlyout(root, minWidth: 280);

        void Pick(SessionsPageChoice choice) {
            Selected = choice;
            flyout.Hide();
            Focus(NavigationMethod.Pointer);
        }
        var matches = new List<SessionsPageChoice>();
        void Narrow() {
            var q = search.Text?.Trim() ?? "";
            matches = [.. (Choices ?? []).Where(c => q.Length == 0 || c.Label.Contains(q, StringComparison.OrdinalIgnoreCase))];
            rows.ItemsSource = matches;
        }
        rows.ItemTemplate = new FuncDataTemplate<SessionsPageChoice>((choice, _) =>
            choice is null ? null : this.ChoiceRow(choice.Label, choice == Selected, () => Pick(choice)));
        search.TextChanged += (_, _) => Narrow();
        search.KeyDown += (_, e) => {
            if (e.Key == Key.Enter && matches.Count > 0) {
                e.Handled = true;
                Pick(matches[0]);
            } else if (e.Key == Key.Down) {
                e.Handled = true;
                FlyoutKeyboard.FocusFirstItem(rows, NavigationMethod.Directional);
            }
        };
        root.KeyDown += (_, e) => FlyoutKeyboard.DismissOnEscape(flyout, this, e, NavigationMethod.Directional);
        flyout.Opened += (_, _) => search.Focus(NavigationMethod.Pointer);
        Narrow();
        flyout.ShowAt(this);
    }
}
