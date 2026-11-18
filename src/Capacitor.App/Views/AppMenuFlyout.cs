using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Capacitor.App.Views;

/// Draws the app's native menus as kcap rows where the OS draws no menu bar, so Windows and Linux
/// offer the same items as the macOS menu bar from the one definition in <see cref="AppMenu"/> and
/// <see cref="AppMenuBar"/>. Each top-level menu becomes a section; nested submenus are not drawn.
public static class AppMenuFlyout {
    /// The application's menu, then each menu on the window's bar.
    public static IReadOnlyList<NativeMenu> MenusFor(Window window) {
        var menus = new List<NativeMenu>();
        if (Application.Current is { } app && NativeMenu.GetMenu(app) is { } appMenu) menus.Add(appMenu);
        if (NativeMenu.GetMenu(window) is { } bar) {
            foreach (var top in bar.Items.OfType<NativeMenuItem>()) {
                if (top.Menu is { } menu) menus.Add(menu);
            }
        }
        return menus;
    }

    /// Replaces the rows with the visible items, read now. Enabled state stays live until the result
    /// is disposed. A click runs <paramref name="close"/> before the item, which may open a window.
    public static IDisposable Populate(Panel rows, IEnumerable<NativeMenu> menus, Action close) {
        rows.Children.Clear();
        var bindings = new CompositeDisposable();
        var separate = false;
        foreach (var menu in menus) {
            separate |= rows.Children.Count > 0;
            foreach (var entry in menu.Items) {
                if (entry is NativeMenuItemSeparator) {
                    separate |= rows.Children.Count > 0;
                    continue;
                }
                if (entry is not NativeMenuItem { IsVisible: true, Menu: null } item) continue;
                if (separate) rows.Children.Add(Separator());
                separate = false;
                rows.Children.Add(Row(item, close, bindings));
            }
        }
        return bindings;
    }

    /// Moves focus to the next or previous enabled row, wrapping, as arrow keys do in a menu.
    public static bool MoveFocus(Panel rows, bool down) {
        var buttons = rows.Children.OfType<Button>().Where(b => b.IsEffectivelyEnabled).ToList();
        if (buttons.Count == 0) return false;
        var at = buttons.FindIndex(b => b.IsFocused);
        var next = at < 0 ? (down ? 0 : buttons.Count - 1) : (at + (down ? 1 : buttons.Count - 1)) % buttons.Count;
        return buttons[next].Focus(NavigationMethod.Directional);
    }

    static Button Row(NativeMenuItem item, Action close, CompositeDisposable bindings) {
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 24 };
        body.Children.Add(new TextBlock { Text = item.Header, VerticalAlignment = VerticalAlignment.Center });
        var gesture = item.Gesture?.ToString("p", null);
        if (gesture is not null) {
            var shortcut = new TextBlock { Text = gesture, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            shortcut.Bind(TextBlock.ForegroundProperty, shortcut.GetResourceObservable("KcapMutedBrush"));
            Grid.SetColumn(shortcut, 1);
            body.Children.Add(shortcut);
        }
        var row = new Button {
            Content = body, Padding = new Thickness(10, 8), FontSize = 13.5,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        row.Classes.Add("kcapGhost");
        AutomationProperties.SetName(row, item.Header);
        if (gesture is not null) AutomationProperties.SetAcceleratorKey(row, gesture);
        bindings.Add(row.Bind(InputElement.IsEnabledProperty, item.GetObservable(NativeMenuItem.IsEnabledProperty)));
        // Hover moves focus, so the pointer and the arrow keys never highlight two rows at once.
        row.PointerEntered += (_, _) => row.Focus(NavigationMethod.Directional);
        row.Click += (_, e) => {
            e.Handled = true;
            close();
            ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
        };
        return row;
    }

    static Border Separator() {
        var line = new Border { Height = 1, Margin = new Thickness(8, 4) };
        line.Bind(Border.BackgroundProperty, line.GetResourceObservable("KcapBorderBrush"));
        return line;
    }
}
