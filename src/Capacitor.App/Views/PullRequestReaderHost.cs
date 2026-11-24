using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public sealed class PullRequestReaderHost : ContentControl {
    internal const string UnifiedReaderResource = "KcapUnifiedPullRequestReader";
    static readonly StyledProperty<bool> UnifiedProperty =
        AvaloniaProperty.Register<PullRequestReaderHost, bool>("Unified");

    protected override Type StyleKeyOverride => typeof(ContentControl);

    IDisposable? _gate;
    PullRequestContextViewModel? _model;
    bool _active;

    internal void Show(PullRequestContextViewModel? model, bool active) {
        _active = active;
        if (active && model is not null && _model is null) {
            _model = model;
            _gate = Bind(UnifiedProperty, this.GetResourceObservable(UnifiedReaderResource));
            UpdateReader();
        }
        if (Content is Control reader) reader.IsVisible = active;
    }

    internal void Reset() {
        _active = false;
        var model = _model;
        _model = null;
        _gate?.Dispose();
        _gate = null;
        model?.SetUnifiedReader(false);
        if (Content is Control reader) reader.DataContext = null;
        Content = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == UnifiedProperty) UpdateReader();
    }

    void UpdateReader() {
        if (_model is not { } model) return;
        var unified = GetValue(UnifiedProperty);
        if (unified ? Content is UnifiedPullRequestReader : Content is PullRequestReader) return;
        var old = Content as Control;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var restoreFocus = _active && old is not null && focused is not null
            && (ReferenceEquals(focused, old) || old.IsVisualAncestorOf(focused));
        if (old is not null) old.DataContext = null;
        var page = model.SetUnifiedReader(unified);
        Control next = page is not null
            ? new UnifiedPullRequestReader { DataContext = page }
            : new PullRequestReader { DataContext = model };
        next.IsVisible = _active;
        Content = next;
        if (!restoreFocus) return;
        Dispatcher.UIThread.Post(() => {
            if (!_active || !ReferenceEquals(Content, next) || TopLevel.GetTopLevel(this) is not { } top) return;
            var current = top.FocusManager?.GetFocusedElement() as Visual;
            if (current is not null && current.IsAttachedToVisualTree() && current.IsEffectivelyVisible && !this.IsVisualAncestorOf(current)) return;
            if (next is UnifiedPullRequestReader reader) reader.FocusReader();
            else ((PullRequestReader)next).FocusReader();
        }, DispatcherPriority.Loaded);
    }
}
