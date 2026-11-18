using System.Globalization;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Receives a workspace model from navigation and retains each tab's native control.
/// TerminalControl owns terminal resizing, including font metrics and reattachment.
public partial class WorkspaceView : UserControl {
    IDisposable? _tabFocus;

    // The workspace header doubles as the draggable chrome on its side of the split — see
    // WindowChrome.
    void OnHeaderPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) =>
        WindowChrome.BeginDrag(this, e);

    public WorkspaceView() {
        InitializeComponent();
        ContextPaneSizing.Attach(this, SplitGrid.ColumnDefinitions[1], WorkContextHost, WorkContextHost, ContextSplitter,
            ContextShowButton);
        RailClearance.Attach(this, SessionHeader);
        TerminalClipboard.Attach(this, TerminalHost);
        // The control draws its caret and takes keystrokes only while focused; a Model assignment
        // is the "terminal became live" moment — but only the Terminal tab may take focus, or a
        // reattach under the Chat tab would steal it from the composer.
        TerminalHost.PropertyChanged += (_, e) => {
            if (e.Property == SvcSystems.UI.Terminal.TerminalControl.ModelProperty && TerminalHost.Model is not null
                && DataContext is WorkspaceViewModel { IsTerminalActive: true })
                TerminalHost.Focus();
        };
        // The PR reader is created on first use, including for sessions without a PTY.
        DataContextChanged += (_, _) => {
            _tabFocus?.Dispose();
            PullRequestHost.Content = null;
            ArtefactsHost.Content = null;
            var model = DataContext as WorkspaceViewModel;
            _tabFocus = model is null ? null : model
                .WhenAnyValue(vm => vm.ActiveTab, vm => vm.Chat)
                .Subscribe(pair => Dispatcher.UIThread.Post(() => {
                    if (!ReferenceEquals(model, DataContext) || model?.ActiveTab != pair.Property1) return;
                    if (pair.Property1 == WorkspaceTab.PullRequest && PullRequestHost.Content is null && model?.PullRequests is { } pullRequests)
                        PullRequestHost.Content = new PullRequestReader { DataContext = pullRequests };
                    if (pair.Property1 == WorkspaceTab.Artefacts && ArtefactsHost.Content is null && model is not null)
                        ArtefactsHost.Content = new ArtefactsView { DataContext = model.Artefacts };
                    if (PullRequestHost.Content is PullRequestReader reader) reader.IsVisible = pair.Property1 == WorkspaceTab.PullRequest;
                    if (pair.Property1 == WorkspaceTab.Chat && pair.Property2 is not null) ChatHost.FocusComposer();
                    else if (pair.Property1 == WorkspaceTab.Terminal) TerminalHost.Focus();
                }, DispatcherPriority.Loaded));
        };
    }
}

/// ITerminalSurface -&gt; TerminalControlModel? bridge for TerminalHost's Model binding.
/// ITerminalSurface (the VM-facing seam, deliberately Avalonia/SvcSystems-free per its own doc
/// comment) has no Model member, so a plain property-path binding can't reach it -- this converter
/// downcasts to the production XtermTerminalSurface and returns null for anything else (no
/// surface yet, or a test's FakeTerminalSurface), which TerminalControl already renders as an
/// empty pane (its own Render short-circuits on a null Model).
public sealed class TerminalSurfaceModelConverter : IValueConverter {
    public static readonly TerminalSurfaceModelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as XtermTerminalSurface)?.Model;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

