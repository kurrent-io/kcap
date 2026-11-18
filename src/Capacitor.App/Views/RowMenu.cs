using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The right-click menu over a list's rows, the buttons carrying `rowClass`. Anchored to the list rather
/// than a row, because a live re-sort recycles row containers and would close a menu owned by one.
/// The row it opened on carries `menuOpen` until it closes, so the row stays highlighted.
public sealed class RowMenu {
    public const string OpenClass = "menuOpen";

    readonly Control _list;
    readonly string _rowClass;
    readonly Func<object?, IReadOnlyList<RowMenuItem>> _itemsFor;
    readonly Flyout _flyout = new() { Placement = PlacementMode.Pointer };
    readonly ItemsControl _items = new() { Margin = new(6), MinWidth = 188 };
    Button? _anchor;

    public RowMenu(Control list, string rowClass, Func<object?, IReadOnlyList<RowMenuItem>> itemsFor) {
        _list = list;
        _rowClass = rowClass;
        _itemsFor = itemsFor;
        _items.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 2 });
        _items.ItemTemplate = new FuncDataTemplate<RowMenuItem>((item, _) => ItemButton(item));
        _flyout.FlyoutPresenterClasses.Add("kcapPanel");
        _flyout.Content = _items;
        // Pointer-opened: focus for Escape without the keyboard focus ring, once the items are realized.
        _flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => FlyoutKeyboard.FocusFirstItem(_items, NavigationMethod.Pointer), DispatcherPriority.Loaded);
        _flyout.Closed += (_, _) => _anchor?.Classes.Remove(OpenClass);
        _items.AddHandler(InputElement.KeyDownEvent,
            (_, e) => FlyoutKeyboard.DismissOnEscape(_flyout, (Control?)_anchor ?? _list, e, NavigationMethod.Pointer), RoutingStrategies.Bubble);
        list.AddHandler(Control.ContextRequestedEvent, OnContextRequested);
    }

    void OnContextRequested(object? sender, ContextRequestedEventArgs e) {
        var row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault(b => b.Classes.Contains(_rowClass));
        if (row is null) return;
        e.Handled = true;
        var items = _itemsFor(row.DataContext);
        if (items.Count == 0) return;
        _anchor?.Classes.Remove(OpenClass);
        _anchor = row;
        row.Classes.Add(OpenClass);
        _items.ItemsSource = items;
        _flyout.ShowAt(_list, showAtPointer: true);
    }

    Button ItemButton(RowMenuItem item) {
        var button = new Button {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new(10, 8),
            FontSize = 13.5,
            Content = new StackPanel {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children = {
                    new AppIcon { Kind = item.Icon, Size = 14 },
                    new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Classes.Add("kcapGhost");
        Avalonia.Automation.AutomationProperties.SetName(button, item.Label);
        button.Click += (_, e) => {
            _flyout.Hide();
            item.Run();
            e.Handled = true;
        };
        return button;
    }
}
