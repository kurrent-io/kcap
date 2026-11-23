using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The launcher pane: DataContext is supplied externally (a plainly-constructed HomeViewModel);
/// this view never builds its own ViewModel.
public partial class LauncherPaneView : UserControl {
    public static readonly StyledProperty<HistoryImportBannerViewModel?> HistoryImportProperty =
        AvaloniaProperty.Register<LauncherPaneView, HistoryImportBannerViewModel?>(nameof(HistoryImport));

    public HistoryImportBannerViewModel? HistoryImport {
        get => GetValue(HistoryImportProperty);
        set => SetValue(HistoryImportProperty, value);
    }

    public LauncherPaneView() => InitializeComponent();

    public void FocusComposer() => Composer.FocusComposer();

    void OnHistoryImportDismissClick(object? sender, RoutedEventArgs e) => FocusComposer();

    /// New session pressed while this pane is already showing.
    public void Nudge() => Composer.Nudge();

    internal bool IsNudgingForTesting => Composer.IsNudgingForTesting;

    IDisposable? _historyLease;
    bool _historyAttached;

    void BindHistoryNotice() {
        _historyLease?.Dispose();
        _historyLease = _historyAttached ? (DataContext as HomeViewModel)?.AttachHistoryImport(HistoryImport) : null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == HistoryImportProperty) BindHistoryNotice();
    }

    protected override void OnDataContextChanged(EventArgs e) {
        base.OnDataContextChanged(e);
        BindHistoryNotice();
    }

    internal Task? PendingIntakeForTesting => Composer.PendingIntakeForTesting;

    internal Flyout? OpenMenuForTesting => Composer.OpenMenuForTesting;

    /// Paired with the visual tree rather than the constructor: a pane swap detaches and
    /// re-attaches the same view, and a behaviour disposed on the way out has to come back with it.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _historyAttached = true;
        BindHistoryNotice();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _historyAttached = false;
        _historyLease?.Dispose();
        _historyLease = null;
        base.OnDetachedFromVisualTree(e);
    }
}

/// Repo chip tip: full path when selected, otherwise the control's job label.
public sealed class RepositoryChipTipConverter : IValueConverter {
    public static readonly RepositoryChipTipConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? path : "Repository for the new session";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MachineLabelConverter : IValueConverter {
    public static readonly MachineLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } name ? name : "Machine";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// BaseRefChip's spoken name: "Branch from · …", HEAD when nothing is chosen.
public sealed class BaseRefLabelConverter : IValueConverter {
    public static readonly BaseRefLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        $"Branch from · {(value is string { Length: > 0 } name ? name : "HEAD")}";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// StartButton's tip: an upload in flight owns the button, so it says so rather than leaving the
/// repository-gate wording standing over a control the user cannot use.
public sealed class StartButtonTipConverter : IMultiValueConverter {
    public static readonly StartButtonTipConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [_, true] ? "Uploading…" : values is [string tip, ..] ? tip : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
