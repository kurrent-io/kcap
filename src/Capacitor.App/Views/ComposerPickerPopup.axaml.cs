using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Capacitor.App.ViewModels.Composer;

namespace Capacitor.App.Views;

/// The / and @ picker flyout over a composer TextBox.
public partial class ComposerPickerPopup : UserControl {
    readonly Flyout _flyout;
    TextBox _input = null!;
    Func<ComposerPicker?> _picker = () => null;
    int _active;

    public ComposerPickerPopup() {
        InitializeComponent();
        Rows.ContainerPrepared += (_, e) => e.Container.Classes.Set("active", e.Index == _active);
        Rows.ContainerIndexChanged += (_, e) => e.Container.Classes.Set("active", e.NewIndex == _active);
        _flyout = new Flyout { Content = this, Placement = PlacementMode.TopEdgeAlignedLeft, ShowMode = FlyoutShowMode.Transient, VerticalOffset = -6 };
        _flyout.FlyoutPresenterClasses.Add("kcapPanel");
    }

    /// The host routes its composer's tunnelled KeyDown through <see cref="HandleKey"/> first, so the
    /// picker sees Enter before the host sends with it.
    public static ComposerPickerPopup Attach(TextBox input, Func<ComposerPicker?> picker) {
        var popup = new ComposerPickerPopup { _input = input, _picker = picker };
        // Posted: the caret moves after TextChanged, and the query is the text before it.
        input.TextChanged += (_, _) => Dispatcher.UIThread.Post(popup.Update);

        return popup;
    }

    async void Update() {
        if (_picker() is not { } picker) return;
        var text = _input.Text ?? "";
        try {
            await picker.UpdateAsync(text, _input.CaretIndex);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: composer picker: {ex.Message}");
            picker.Dismiss();
        }
        if ((_input.Text ?? "") != text) return;

        if (picker.Rows.Count == 0 || !_input.IsFocused) {
            _flyout.Hide();
            return;
        }
        _active = 0;
        Rows.ItemsSource = picker.Rows;
        Scroller.Width = _input.Bounds.Width;
        Scroller.Offset = default;
        if (_flyout.IsOpen) return;
        _flyout.ShowAt(_input);
        _input.Focus();
    }

    public bool HandleKey(KeyEventArgs e) {
        if (!_flyout.IsOpen || e.KeyModifiers != KeyModifiers.None || _picker() is not { Rows.Count: > 0 and var count } picker) return false;
        switch (e.Key) {
            case Key.Up or Key.Down:
                Rows.ContainerFromIndex(_active)?.Classes.Set("active", false);
                _active = (_active + (e.Key == Key.Down ? 1 : -1) + count) % count;
                Rows.ScrollIntoView(_active);
                Rows.ContainerFromIndex(_active)?.Classes.Set("active", true);
                return true;
            case Key.Enter or Key.Tab:
                Pick(picker.Rows[Math.Min(_active, count - 1)]);
                return true;
            case Key.Escape:
                picker.Dismiss();
                _flyout.Hide();
                return true;
            default:
                return false;
        }
    }

    void OnRowClick(object? sender, RoutedEventArgs e) => Pick(((Button)sender!).DataContext!);

    void Pick(object row) {
        if (row is not FileMention { Path: [.., '/'] }) _flyout.Hide();
        var (text, caret) = ComposerPicker.Insert(row, _input.Text ?? "", _input.CaretIndex);
        _input.Text = text;
        _input.CaretIndex = caret;
        _input.Focus();
    }
}
