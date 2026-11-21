using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// A focused text box's caret blinks only for <see cref="Timeout"/> after the last input, then
/// stays solid until the next. Each blink toggle wakes several render frames, which is most of
/// what an idle app with a focused composer spends. Installed once per application.
public static class CaretBlink {
    /// GTK's cursor-blink-timeout default.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    static bool _installed;
    static DispatcherTimer? _timer;
    static TextBox? _current;
    static TextPresenter? _frozen;

    public static void Install() {
        if (_installed) return;
        _installed = true;
        InputElement.GotFocusEvent.AddClassHandler<TextBox>((box, _) => Wake(box), handledEventsToo: true);
        InputElement.LostFocusEvent.AddClassHandler<TextBox>((box, _) => Release(box), handledEventsToo: true);
        const RoutingStrategies tunnel = RoutingStrategies.Tunnel;
        InputElement.KeyDownEvent.AddClassHandler<TextBox>((box, _) => Wake(box), tunnel, handledEventsToo: true);
        InputElement.TextInputEvent.AddClassHandler<TextBox>((box, _) => Wake(box), tunnel, handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<TextBox>((box, _) => Wake(box), tunnel, handledEventsToo: true);
    }

    static void Wake(TextBox box) {
        if (!box.IsFocused) return;
        if (_current != box) Release(_current);
        _current = box;
        Thaw(box);
        _timer ??= new DispatcherTimer(Timeout, DispatcherPriority.Background, (_, _) => Settle());
        _timer.Stop();
        _timer.Start();
    }

    static void Release(TextBox? box) {
        if (box is null || box != _current) return;
        _timer?.Stop();
        Thaw(box);
        _current = null;
    }

    internal static void Settle() {
        _timer?.Stop();
        if (_current is not { IsFocused: true } box || Presenter(box) is not { } presenter) return;
        presenter.SetCurrentValue(TextPresenter.CaretBlinkIntervalProperty, TimeSpan.Zero);
        // With no blink timer the caret keeps whatever phase it stopped on; this draws it.
        presenter.ShowCaret();
        _frozen = presenter;
    }

    static void Thaw(TextBox box) {
        if (_frozen is not { } presenter) return;
        _frozen = null;
        presenter.SetCurrentValue(TextPresenter.CaretBlinkIntervalProperty, box.CaretBlinkInterval);
        // The rebuilt blink timer starts stopped. A presenter a new template replaced would blink
        // detached, with nothing left to stop it.
        if (box.IsFocused && Presenter(box) == presenter) presenter.ShowCaret();
    }

    static TextPresenter? Presenter(TextBox box) => box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
}
