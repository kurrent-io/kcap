using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Capacitor.App.ViewModels;
using ReactiveUI.Reactive;

namespace Capacitor.App.Views;

/// The right-click menu on a rail session row, in the same `kcapPanel` of ghost rows as the rail's
/// other pickers. One flyout serves every row: it acts on whichever row opened it.
public static class SessionRowMenu {
    sealed record Item(string Name, Func<RailSessionViewModel, string> Label, Func<RailSessionViewModel, ReactiveCommand<Unit, Unit>> Command);

    static readonly Item[] Items = [
        new("SessionMenuRename", _ => "Rename…", vm => vm.RenameCommand),
        new("SessionMenuMakePrivate", _ => "Make private", vm => vm.MakePrivateCommand),
        new("SessionMenuDelete", _ => "Delete from kcap…", vm => vm.DeleteCommand),
        new("SessionMenuTerminate", vm => vm.TerminateLabel, vm => vm.TerminateCommand),
    ];

    public static FlyoutBase Flyout { get; } = Build();

    static Flyout Build() {
        var flyout = new Flyout();
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        var panel = new StackPanel { Spacing = 2, Margin = new(6), MinWidth = 180 };
        var buttons = new List<(Button Button, Item Item)>();
        foreach (var item in Items) {
            var button = new Button {
                Name = item.Name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new(10, 6),
                FontSize = 13.5,
            };
            button.Classes.Add("kcapGhost");
            // Hiding the flyout clears the rows' inherited DataContext, so the row is read first.
            button.Click += (_, e) => {
                var row = flyout.Target?.DataContext as RailSessionViewModel;
                flyout.Hide();
                e.Handled = true;
                if (row is null) return;
                var command = item.Command(row);
                if (((System.Windows.Input.ICommand)command).CanExecute(null)) command.Execute().Subscribe(_ => { }, _ => { });
            };
            panel.Children.Add(button);
            buttons.Add((button, item));
        }
        flyout.Content = panel;
        panel.AddHandler(InputElement.KeyDownEvent, (_, e) => {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            flyout.Hide();
        }, RoutingStrategies.Bubble);
        // A row revised while the menu is open, by a session id arriving or an agent ending, updates its items in place.
        var watched = new List<IDisposable>();
        void Unwatch() {
            foreach (var subscription in watched) subscription.Dispose();
            watched.Clear();
        }
        flyout.Opening += (_, _) => {
            Unwatch();
            if (flyout.Target?.DataContext is not RailSessionViewModel { HasMenu: true } row) {
                flyout.Hide();
                return;
            }
            foreach (var (button, item) in buttons) {
                button.Content = item.Label(row);
                watched.Add(item.Command(row).CanExecute.Subscribe(can => button.IsEnabled = can));
            }
        };
        flyout.Closed += (_, _) => Unwatch();
        flyout.Opened += (_, _) => buttons.Select(b => b.Button).FirstOrDefault(b => b.IsEffectivelyEnabled)?.Focus();
        return flyout;
    }
}
