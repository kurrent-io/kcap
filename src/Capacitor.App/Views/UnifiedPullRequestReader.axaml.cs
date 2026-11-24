using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public sealed partial class UnifiedPullRequestReader : UserControl {
    readonly (string Key, Control Region, Control Heading)[] _sections;
    readonly UnifiedPullRequestGroupView[] _groups;
    UnifiedPullRequestPage? _page;
    bool _layoutQueued;
    bool _restore;
    bool _positioning;

    public UnifiedPullRequestReader() {
        InitializeComponent();
        _groups = [Reviewers, SubmittedReviews, FileThreads, GeneralComments, Checks];
        _sections = [
            ("overview", Overview, OverviewHeading),
            ("conflicts", Conflicts, ConflictsHeading),
            ("description", DescriptionSection, DescriptionHeading),
            ("reviewers", Reviewers, Reviewers.Heading),
            ("reviews", SubmittedReviews, SubmittedReviews.Heading),
            ("threads", FileThreads, FileThreads.Heading),
            ("conversation", GeneralComments, GeneralComments.Heading),
            ("checks", Checks, Checks.Heading),
        ];
        DataContextChanged += (_, _) => SetPage(DataContext as UnifiedPullRequestPage);
        PageScroll.ScrollChanged += (_, e) => {
            if (_positioning || !CanTrack) return;
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) QueueLayout(restore: true);
            else if (e.OffsetDelta.Y != 0 && !_restore) {
                RecordAnchor();
                QueueLayout();
            }
        };
    }

    bool CanTrack => _page is { ShowContent: true } && this.IsAttachedToVisualTree() && IsEffectivelyVisible;

    void SetPage(UnifiedPullRequestPage? page) {
        if (_page is { } old) {
            old.RestoreAnchor -= RestoreAnchor;
            old.JumpRequested -= OnJumpRequested;
        }
        _page = page;
        if (page is null) return;
        page.RestoreAnchor += RestoreAnchor;
        page.JumpRequested += OnJumpRequested;
        RestoreAnchor();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        RestoreAnchor();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>()) RestoreAnchor();
    }

    internal void FocusReader() {
        if (_page is { ShowContent: true } page) {
            var heading = _sections.FirstOrDefault(item => item.Key == page.Anchor.Section).Heading;
            if (heading is { IsEffectivelyVisible: true } && heading.Focus(NavigationMethod.Tab)) return;
        }
        if (!OverviewHeading.IsEffectivelyVisible || !OverviewHeading.Focus(NavigationMethod.Tab))
            PageScroll.Focus(NavigationMethod.Tab);
    }

    void OnJump(object? sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string destination }) _page?.Jump(destination);
    }

    void OnJumpRequested(string destination) {
        RestoreAnchor();
        if (CanTrack) FocusDestination(destination);
    }

    void FocusDestination(string destination) {
        var section = _sections.FirstOrDefault(item => item.Key == destination);
        if (section.Heading is { IsEffectivelyVisible: true } heading && heading.Focus(NavigationMethod.Tab))
            _page?.AcknowledgeJumpFocus();
    }

    void RestoreAnchor() => QueueLayout(restore: true);

    void QueueLayout(bool restore = false) {
        _restore |= restore;
        if (_layoutQueued) return;
        _layoutQueued = true;
        Dispatcher.UIThread.Post(() => {
            _layoutQueued = false;
            if (!CanTrack || _page is not { } page) return;
            if (page.PendingJumpFocus is { } destination) {
                PageScroll.UpdateLayout();
                FocusDestination(destination);
            }
            if (_restore) PositionAt(page.Anchor);
            _restore = false;
            RevealVisibleGroups();
        }, DispatcherPriority.Loaded);
    }

    void PositionAt(UnifiedPullRequestPage.ReadingAnchor anchor) {
        _positioning = true;
        try {
            PageScroll.UpdateLayout();
            var target = _sections.FirstOrDefault(section => section.Key == anchor.Section).Region;
            if (target is not { IsEffectivelyVisible: true }) target = Overview;
            var row = (target as UnifiedPullRequestGroupView)?.FindRow(anchor.Row);
            var offset = (row ?? target).TranslatePoint(default, PageScroll)?.Y ?? 0;
            PageScroll.Offset = new Vector(0, anchor is { Section: "overview", Row: null, Offset: 0 }
                ? 0 : Math.Max(0, PageScroll.Offset.Y + offset + (anchor.Row is null || row is not null ? anchor.Offset : 0)));
        } finally {
            _positioning = false;
        }
    }

    void RecordAnchor() {
        if (_page is not { } page) return;
        if (PageScroll.Offset.Y <= 0) {
            page.Anchor = new("overview", null, 0);
            return;
        }
        var top = _sections[0];
        foreach (var section in _sections) {
            if (!section.Region.IsEffectivelyVisible || section.Region.TranslatePoint(default, PageScroll) is not { Y: <= 0 }) continue;
            top = section;
        }
        var row = (top.Region as UnifiedPullRequestGroupView)?.RowAtTop(PageScroll);
        var y = (row ?? top.Region).TranslatePoint(default, PageScroll)?.Y ?? 0;
        page.Anchor = new(top.Key, UnifiedPullRequestGroupView.RowKey(row), -y);
    }

    void RevealVisibleGroups() {
        foreach (var group in _groups) {
            if (group.Heading.TranslatePoint(default, PageScroll) is not { } point) continue;
            if (point.Y + group.Heading.Bounds.Height >= 0 && point.Y <= PageScroll.Viewport.Height)
                (group.DataContext as UnifiedPullRequestGroup)?.RevealIfUnloaded();
        }
    }
}
