using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Holds a still of the content pane over a session switch until the incoming chat has laid out
/// its first read, or over a page docking or expanding until it has laid out, then fades it out. A new workspace fills in over several Background-priority
/// dispatches, each run after a render, so without the still every half-built state is painted in turn.
public sealed class WorkspaceSwitchCover {
    static readonly TimeSpan MaxHold = TimeSpan.FromMilliseconds(400);

    readonly Control _pane;
    readonly Control _cover;
    readonly StillImage _still;
    readonly SerialDisposable _hold = new();
    CancellationTokenSource? _fade;
    int _generation;

    public WorkspaceSwitchCover(Control pane, Control cover, StillImage still) {
        _pane = pane;
        _cover = cover;
        _still = still;
    }

    public IDisposable Track(MainWindowViewModel vm) {
        void OnChanging(object? sender, PropertyChangingEventArgs e) {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentWorkspace)) Capture();
        }
        vm.PropertyChanging += OnChanging;
        var swaps = vm.WhenAnyValue(x => x.CurrentWorkspace).Skip(1).Subscribe(workspace => Hold(workspace, vm.Time));
        var docks = vm.PageDockChanging.Subscribe(_ => CoverNextLayout());
        return Disposable.Create(() => {
            vm.PropertyChanging -= OnChanging;
            swaps.Dispose();
            docks.Dispose();
            Hide();
        });
    }

    // A switch during the fade re-captures, so the still shows the session being left, not the one before it.
    void Capture() {
        if (_cover.IsVisible && _fade is null) return;
        Hide();
        var size = _pane.Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;
        var scale = TopLevel.GetTopLevel(_pane)?.RenderScaling ?? 1;
        var still = new RenderTargetBitmap(PixelSize.FromSize(size, scale), new Vector(96 * scale, 96 * scale));
        still.Render(_pane);
        _still.Source = still;
        _cover.IsVisible = true;
    }

    // For a change made right after this returns that draws in one layout pass.
    void CoverNextLayout() {
        Capture();
        if (_cover.IsVisible) AfterLayout(++_generation);
    }

    void Hold(ISessionWorkspace? workspace, TimeProvider time) {
        if (workspace is null || !_cover.IsVisible) {
            Lift();
            return;
        }
        var generation = ++_generation;
        // Installed before subscribing: a workspace already painted lifts during Subscribe, and that lift must find
        // the hold to dispose. Adding to a disposed hold disposes the addition at once.
        var hold = new CompositeDisposable(time.CreateTimer(_ => Dispatcher.UIThread.Post(() => LiftIfHeld(generation)), null, MaxHold, Timeout.InfiniteTimeSpan));
        _hold.Disposable = hold;
        hold.Add(Painted(workspace).Where(p => p).Take(1).Subscribe(_ => AfterLayout(generation)));
    }

    void LiftIfHeld(int generation) {
        if (generation == _generation) Lift();
    }

    // Two frames: the first lays out the new rows, the second the scroll to their tail.
    void AfterLayout(int generation) {
        if (TopLevel.GetTopLevel(_pane) is not { } top) {
            Lift();
            return;
        }
        top.RequestAnimationFrame(_ => top.RequestAnimationFrame(_ => LiftIfHeld(generation)));
    }

    void Lift() {
        EndHold();
        if (_cover.IsVisible && _fade is null) _ = FadeOut();
    }

    async Task FadeOut() {
        var fade = _fade = new CancellationTokenSource();
        _cover.IsHitTestVisible = false;
        // The animation drops its value when it ends, a frame before the cover is hidden: the end
        // opacity is set underneath it so that frame does not flash the still.
        _cover.Opacity = 0;
        await Motion.Standard.RunAsync(_cover, fade.Token, (Visual.OpacityProperty, 1d, 0d));
        if (!fade.IsCancellationRequested) Hide();
    }

    void EndHold() {
        _generation++;
        _hold.Disposable = null;
    }

    void Hide() {
        EndHold();
        _fade?.Cancel();
        _fade = null;
        _cover.IsVisible = false;
        _cover.ClearValue(Visual.OpacityProperty);
        _cover.ClearValue(InputElement.IsHitTestVisibleProperty);
        var still = _still.Source;
        _still.Source = null;
        still?.Dispose();
    }

    static IObservable<bool> Painted(ISessionWorkspace workspace) => workspace switch {
        WorkspaceViewModel local => Observable.Switch(local
            .WhenAnyValue(w => w.Chat, w => w.IsStarting, (chat, starting) => starting ? Observable.Return(true) : Read(chat))),
        RemoteSessionViewModel remote => Read(remote.Chat),
        _ => Observable.Return(true),
    };

    static IObservable<bool> Read(ChatTabViewModel? chat) =>
        chat is null ? Observable.Return(false) : chat.WhenAnyValue(c => c.Phase).Select(phase => phase != ChatTabPhase.Waiting);
}
