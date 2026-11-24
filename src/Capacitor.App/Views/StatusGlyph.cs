using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

public sealed class StatusGlyph : Decorator {
    public static readonly StyledProperty<StatusGlyphKind?> KindProperty =
        AvaloniaProperty.Register<StatusGlyph, StatusGlyphKind?>(nameof(Kind));
    public static readonly AttachedProperty<bool> IsPreviewProperty =
        AvaloniaProperty.RegisterAttached<StatusGlyph, Control, bool>("IsPreview");

    AppIcon? _icon;
    Ellipse? _dot;
    Panel? _activity;

    public StatusGlyph() {
        IsHitTestVisible = false;
        IsVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    public StatusGlyphKind? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public static bool GetIsPreview(Control control) => control.GetValue(IsPreviewProperty);
    public static void SetIsPreview(Control control, bool value) => control.SetValue(IsPreviewProperty, value);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property != KindProperty && change.Property != IsPreviewProperty) return;
        if (!GetIsPreview(this) || Kind is not { } kind) {
            IsVisible = false;
            Child = null;
            _icon = null;
            _dot = null;
            _activity = null;
            return;
        }

        var (icon, resource) = Appearance(kind);
        var ink = AppPalette.Brush(resource);
        _icon ??= new AppIcon { Size = 12 };
        _icon.Kind = icon;
        _icon.Foreground = ink;
        if (kind == StatusGlyphKind.Activity) {
            if (_activity is null) {
                Child = null;
                _dot = new Ellipse {
                    Width = 4, Height = 4, UseLayoutRounding = false,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                _activity = new Panel { Children = { _icon, _dot } };
            }
            _dot!.Fill = ink;
            Child = _activity;
        } else {
            if (_activity is not null) {
                Child = null;
                _activity.Children.Clear();
                _activity = null;
                _dot = null;
            }
            Child = _icon;
        }
        IsVisible = true;
    }

    public static IValueConverter Install { get; } = new FuncValueConverter<RowState, StatusGlyphKind>(InstallStepRow.GlyphFor);
    public static IValueConverter InstallLabel { get; } = new FuncValueConverter<RowState, string>(InstallStepRow.LabelFor);
    public static IValueConverter Run { get; } = new FuncValueConverter<RunState, StatusGlyphKind>(state => state.Glyph());
    public static IValueConverter Part { get; } = new FuncValueConverter<WorkContextPartMark, StatusGlyphKind>(mark => mark switch {
        WorkContextPartMark.ThisSession => StatusGlyphKind.Here,
        WorkContextPartMark.Settled => StatusGlyphKind.Success,
        _ => StatusGlyphKind.Unknown,
    });

    internal static (LucideIconKind Icon, string Brush) Appearance(StatusGlyphKind kind) => kind switch {
        StatusGlyphKind.Activity => (LucideIconKind.Circle, "KcapActivityBrush"),
        StatusGlyphKind.Pending => (LucideIconKind.Circle, "KcapMutedBrush"),
        StatusGlyphKind.Attention => (LucideIconKind.CircleAlert, "KcapWarningBrush"),
        StatusGlyphKind.Input => (LucideIconKind.CircleQuestionMark, "KcapWarningBrush"),
        StatusGlyphKind.Stale => (LucideIconKind.Clock, "KcapWarningBrush"),
        StatusGlyphKind.Interrupted => (LucideIconKind.CirclePause, "KcapWarningBrush"),
        StatusGlyphKind.Conflict => (LucideIconKind.TriangleAlert, "KcapWarningBrush"),
        StatusGlyphKind.Failure => (LucideIconKind.CircleX, "KcapDangerBrush"),
        StatusGlyphKind.Success or StatusGlyphKind.Healthy => (LucideIconKind.CircleCheck, "KcapSuccessBrush"),
        StatusGlyphKind.Stopped => (LucideIconKind.CircleStop, "KcapMutedBrush"),
        StatusGlyphKind.Skipped => (LucideIconKind.CircleMinus, "KcapMutedBrush"),
        StatusGlyphKind.Here => (LucideIconKind.MapPin, "KcapMutedBrush"),
        StatusGlyphKind.OpenPullRequest => (LucideIconKind.GitPullRequest, "KcapPrOpenBrush"),
        StatusGlyphKind.DraftPullRequest => (LucideIconKind.GitPullRequestDraft, "KcapMutedBrush"),
        StatusGlyphKind.MergedPullRequest => (LucideIconKind.GitMerge, "KcapPrMergedBrush"),
        StatusGlyphKind.ClosedPullRequest => (LucideIconKind.GitPullRequestClosed, "KcapPrClosedBrush"),
        StatusGlyphKind.Commented => (LucideIconKind.MessageSquare, "KcapMutedBrush"),
        _ => (LucideIconKind.CircleQuestionMark, "KcapMutedBrush"),
    };
}
