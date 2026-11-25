using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// <summary>
/// Chat rows have very different heights. Keep each measured height when its container is
/// recycled, rather than estimating the whole history from the rows currently on screen.
/// Unknown rows use a fixed estimate; discovering one never resizes other unknown rows.
/// Rows inside a chat topic are indented by its rail, and clicking the rail folds the topic.
/// </summary>
public sealed class ChatVirtualizingPanel : VirtualizingPanel {
    readonly record struct Row(object Item, Control Container, object? RecycleKey, bool OwnContainer);
    const double EstimatedHeight = 80;
    const double KeepAliveOverscan = 240;
    const int MessagePoolLimit = 12;
    internal const double RailWidth = 16;
    // Centres the rail under a topic header's 14px icon.
    const double RailInset = 7;
    // From the middle of a topic header to just below its icon.
    const double HeaderIconClearance = 9;
    readonly Dictionary<object, double> _measured = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<int, Row> _realized = [];
    readonly Dictionary<object, Stack<Control>> _pool = [];
    // Prompt and reply rows parked attached and hidden, so taking one back skips styling a fresh row.
    List<ChatRowContainer>? _messagePool;
    double[] _starts = [0];
    Rect _viewport;
    double _lastWidth = double.NaN;
    int _lastReadingLayout;
    bool _resetReadingMeasurements;
    internal bool ReadingLayoutAnchorPending { get; private set; }
    int _first = -1;
    int _last = -1;
    ChatTopicRailLayer? _rails;
    /// After rows go in above the ones on screen: the row the reader was at, how far into it the viewport's
    /// top was, the offset's distance from the viewport's top, and the offset when this was set. The offset
    /// follows that row once layout has measured it, since rows measured around it move it from where the
    /// estimates put it; a scroll the reader made before then is carried along rather than overwritten.
    (object Item, double Within, double Inset, double Offset)? _insertAnchor;
    bool _insertArranged;
    /// How far the viewport moved for an insertion, until a viewport reflecting it arrives: one reported
    /// from before would measure the inserted rows at the top and move every row below them.
    double _shiftInFlight;

    internal bool MeasuredAtEstimatedEnd { get; private set; }

    public static readonly StyledProperty<double> LaneWidthProperty =
        AvaloniaProperty.Register<ChatVirtualizingPanel, double>(nameof(LaneWidth), double.PositiveInfinity);

    public static readonly StyledProperty<int> ReadingLayoutProperty =
        AvaloniaProperty.Register<ChatVirtualizingPanel, int>(nameof(ReadingLayout));

    static ChatVirtualizingPanel() => AffectsMeasure<ChatVirtualizingPanel>(LaneWidthProperty, ReadingLayoutProperty);

    public int ReadingLayout {
        get => GetValue(ReadingLayoutProperty);
        set => SetValue(ReadingLayoutProperty, value);
    }

    internal void InvalidateReadingMeasurements() {
        if (ReadingLayout == 0) return;
        _resetReadingMeasurements = true;
        InvalidateMeasure();
    }

    /// How wide the agent's rows may be.
    public double LaneWidth {
        get => GetValue(LaneWidthProperty);
        set => SetValue(LaneWidthProperty, value);
    }

    public ChatVirtualizingPanel() => EffectiveViewportChanged += OnViewportChanged;

    // Added first so every row draws over it; it takes the pointer only in the rail gutters.
    void EnsureRailLayer() {
        if (_rails is not null && Children.Contains(_rails)) return;
        _rails ??= new ChatTopicRailLayer(this);
        AddInternalChild(_rails);
        Children.Move(Children.IndexOf(_rails), 0);
    }

    void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e) {
        if (_shiftInFlight != 0) {
            if (Math.Abs(e.EffectiveViewport.Top + _shiftInFlight - _viewport.Top) < 1) return;
            _shiftInFlight = 0;
        }
        if (_viewport == e.EffectiveViewport) return;
        var old = _viewport;
        _viewport = e.EffectiveViewport;
        ReportTop();
        if (old.Size != _viewport.Size || _first < 0 || _last >= Items.Count
            || Math.Max(0, _viewport.Top) < _starts[_first]
            || Math.Min(_starts[^1], _viewport.Bottom) > _starts[_last + 1]) InvalidateMeasure();
    }

    void RebuildStarts() {
        _starts = new double[Items.Count + 1];
        for (var i = 0; i < Items.Count; i++)
            _starts[i + 1] = _starts[i] + (Items[i] is { } item ? _measured.GetValueOrDefault(item, EstimatedHeight) : EstimatedHeight);
    }

    int IndexAt(double y) {
        var index = Array.BinarySearch(_starts, y);
        return Math.Clamp(index >= 0 ? index : ~index - 1, 0, Math.Max(0, Items.Count - 1));
    }

    /// A user's turn spans the column to sit at its right edge; the agent's rows keep to the lane.
    static bool SpansColumn(object item) => item is UserTurnItem or ShellCommandItem or ChatTopicItem or ChatTopicEndItem;

    // Every row keeps the rail's space, so marking a topic never shifts its messages sideways; a
    // topic's header and end sit out over the rail.
    static double Indent(object item) => item is ChatTopicItem or ChatTopicEndItem ? 0 : RailWidth;

    double RowWidth(object item, double width) {
        var inner = Math.Max(0, width - Indent(item));
        return SpansColumn(item) ? inner : Math.Min(inner, Math.Max(0, LaneWidth - Indent(item)));
    }

    protected override Size MeasureOverride(Size availableSize) {
        var scroll = this.FindAncestorOfType<ScrollViewer>();
        var view = this.FindAncestorOfType<ChatTabView>();
        MeasuredAtEstimatedEnd = scroll is not null && scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 2;
        EnsureRailLayer();
        var readingLayout = ReadingLayout;
        var reset = readingLayout != _lastReadingLayout || _resetReadingMeasurements;
        _lastReadingLayout = readingLayout;
        _resetReadingMeasurements = false;
        if (Items.Count == 0) {
            if (reset) _measured.Clear();
            _first = _last = -1;
            return default;
        }
        if (_starts.Length != Items.Count + 1) RebuildStarts();
        if (reset) ResetHeights(view, scroll);
        var anchor = IndexAt(Math.Max(0, _viewport.Top));
        var buffered = _first >= 0 && _last < Items.Count
            && Math.Max(0, _viewport.Top) >= _starts[_first]
            && Math.Min(_starts[^1], _viewport.Bottom) <= _starts[_last + 1];
        var first = buffered ? _first : IndexAt(Math.Max(0, _viewport.Top));
        var end = Math.Max(_viewport.Bottom, _viewport.Top + 1);
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var captured = view?.CapturedTranscriptElement;
        var scrollAnchor = this.FindAncestorOfType<IScrollAnchorProvider>()?.CurrentAnchor;
        if (!reset && availableSize.Width != _lastWidth && scrollAnchor is not null && scroll is not null)
            PreserveResizeAnchor(scroll, scrollAnchor);
        _lastWidth = availableSize.Width;
        var overscan = view?.IsSmoothScrolling == true ? KeepAliveOverscan : 0;
        // Keep the presenter's anchor until arrange has compensated for any newly measured rows.
        foreach (var entry in _realized.ToArray()) {
            if ((entry.Key < first || entry.Key > anchor && _starts[entry.Key] >= end) && !KeepAlive(entry.Value, focused, captured, scrollAnchor, overscan))
                Unrealize(entry.Key, recycleMessage: true);
        }
        var last = first;
        var width = 0.0;
        var adjustment = 0.0;
        var previousStart = _starts[first];
        for (var i = first; i < Items.Count && (i <= anchor || previousStart + adjustment < end); i++) {
            var nextStart = _starts[i + 1];
            var oldHeight = nextStart - previousStart;
            _starts[i] = previousStart + adjustment;
            previousStart = nextStart;
            var row = Realize(i);
            row.Container.Measure(new Size(RowWidth(row.Item, availableSize.Width), double.PositiveInfinity));
            var height = row.Container.DesiredSize.Height;
            adjustment += height - oldHeight;
            _measured[row.Item] = height;
            width = Math.Max(width, row.Container.DesiredSize.Width);
            last = i;
        }
        if (adjustment != 0)
            for (var i = last + 1; i < _starts.Length; i++) _starts[i] += adjustment;
        _first = first;
        _last = last;
        foreach (var entry in _realized.ToArray()) {
            if (entry.Key >= first && entry.Key <= last) continue;
            if (!KeepAlive(entry.Value, focused, captured, scrollAnchor, overscan)) {
                Unrealize(entry.Key, recycleMessage: true);
                continue;
            }
            // A warm row above the viewport keeps its stored height. Remeasuring it shifts every
            // start below it, and the presenter follows by moving the reader's offset.
            if (entry.Key < first && !IsPinned(entry.Value, focused, captured, scrollAnchor)) continue;
            entry.Value.Container.Measure(new Size(RowWidth(entry.Value.Item, availableSize.Width), double.PositiveInfinity));
            var height = entry.Value.Container.DesiredSize.Height;
            var delta = height - (_starts[entry.Key + 1] - _starts[entry.Key]);
            _measured[entry.Value.Item] = height;
            if (delta != 0)
                for (var i = entry.Key + 1; i < _starts.Length; i++) _starts[i] += delta;
        }
        return new Size(width, _starts[^1]);
    }

    void ResetHeights(ChatTabView? view, ScrollViewer? scroll) {
        var reading = IndexAt(Math.Max(0, _viewport.Top));
        var within = Math.Max(0, _viewport.Top - _starts[reading]);
        var item = Items[reading];
        var pending = _insertAnchor;
        _measured.Clear();
        RebuildStarts();
        _first = _last = -1;
        if (scroll is null || item is null || view?.ShouldAnchorReadingLayout != true && pending is null) return;

        var inset = pending?.Inset ?? scroll.Offset.Y - _viewport.Top;
        _insertAnchor = pending ?? (item, within, inset, scroll.Offset.Y);
        ReadingLayoutAnchorPending = true;
        var top = _starts[reading] + Math.Min(within, EstimatedHeight - 1);
        _shiftInFlight += top - _viewport.Top;
        _viewport = new Rect(_viewport.X, top, _viewport.Width, _viewport.Height);
    }

    void PreserveResizeAnchor(ScrollViewer scroll, Control anchor) {
        var view = this.FindAncestorOfType<ChatTabView>();
        if (view?.CanPreserveReadingPosition != true || anchor.TranslatePoint(default, scroll) is not { } before) return;
        var item = anchor.DataContext;
        var inputVersion = view.ListInputVersion;
        // Child layout can finish before the presenter gets an arrange pass to track its anchor.
        Dispatcher.UIThread.Post(() => {
            if (!view.CanPreserveReadingPosition || view.ListInputVersion != inputVersion
                || !ReferenceEquals(anchor.DataContext, item) || !this.IsVisualAncestorOf(anchor)
                || TopLevel.GetTopLevel(this) is null || anchor.TranslatePoint(default, scroll) is not { } after) return;
            var delta = after.Y - before.Y;
            if (Math.Abs(delta) > 0.5) scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + delta);
        }, DispatcherPriority.Render);
    }

    bool KeepAlive(Row row, Visual? focused, Visual? captured, Control? scrollAnchor, double overscan) =>
        row.Container.Bounds.Intersects(overscan > 0 ? _viewport.Inflate(new Thickness(0, overscan)) : _viewport)
        || IsPinned(row, focused, captured, scrollAnchor);

    // A row holding focus or a pressed pointer stays, so the press and the key that follow reach the
    // message they started on.
    static bool IsPinned(Row row, Visual? focused, Visual? captured, Control? scrollAnchor) =>
        row.OwnContainer || ReferenceEquals(row.Container, scrollAnchor) ||
        ContainsInput(row.Container, focused) || ContainsInput(row.Container, captured);

    static bool ContainsInput(Control container, Visual? target) =>
        target is not null && (ReferenceEquals(container, target) || container.IsVisualAncestorOf(target));

    protected override Size ArrangeOverride(Size finalSize) {
        if (_insertAnchor is not null && !_insertArranged && this.FindAncestorOfType<ScrollViewer>() is { } scroll) {
            _insertArranged = true;
            // Only input moves the offset between the insertion and here; the presenter's own anchoring runs later in this pass.
            var moved = scroll.Offset.Y - _insertAnchor.Value.Offset;
            // The presenter takes the grown extent only after this pass; an offset set now is clamped to the old one.
            void Apply(object? sender, EventArgs e) {
                scroll.LayoutUpdated -= Apply;
                _insertArranged = false;
                if (_insertAnchor is not var (item, within, inset, _)) return;
                _insertAnchor = null;
                var index = -1;
                for (var i = 0; i < Items.Count && index < 0; i++)
                    if (ReferenceEquals(Items[i], item)) index = i;
                if (index >= 0 && _starts.Length == Items.Count + 1) {
                    if (ReadingLayoutAnchorPending)
                        within = Math.Min(within, Math.Max(0, _starts[index + 1] - _starts[index] - 1));
                    scroll.Offset = new Vector(scroll.Offset.X, _starts[index] + within + inset + moved);
                }
                ReadingLayoutAnchorPending = false;
            }
            scroll.LayoutUpdated += Apply;
        }
        var anchors = this.FindAncestorOfType<IScrollAnchorProvider>();
        foreach (var entry in _realized) {
            var container = entry.Value.Container;
            container.Arrange(new Rect(Indent(entry.Value.Item), _starts[entry.Key], RowWidth(entry.Value.Item, finalSize.Width), container.DesiredSize.Height));
            if (container.IsVisible && container.Bounds.Intersects(_viewport)) anchors?.RegisterAnchorCandidate(container);
        }
        _rails?.Arrange(new Rect(finalSize));
        _rails?.InvalidateVisual();
        ReportTop();
        return finalSize;
    }

    // A row whose top has just scrolled out of view is still the one being read.
    const double TopRowDepth = 24;

    void ReportTop() {
        if (Items.Count == 0 || _starts.Length != Items.Count + 1) return;
        this.FindAncestorOfType<ChatTabView>()?.ShowingAtTop(Items[IndexAt(Math.Max(0, _viewport.Top + TopRowDepth))] as ChatItemViewModel);
    }

    internal void DrawRails(DrawingContext context, ChatTopicItem? hovered) {
        var rest = new Pen(Brush("KcapTopicRailBrush"), 1.5);
        var hover = new Pen(Brush("KcapTopicBrush"), 2.5);
        Pen PenFor(ChatTopicItem topic) => ReferenceEquals(topic, hovered) ? hover : rest;

        foreach (var (index, row) in _realized) {
            if (row.Item is not ChatItemViewModel item || !row.Container.IsVisible || index + 1 >= _starts.Length) continue;
            var top = _starts[index];
            var bottom = _starts[index + 1];
            // Past the last row's own gap the rail would read as more chat below.
            if (index == Items.Count - 1 && row.Container is ChatRowContainer { Row: Layoutable { Margin.Bottom: > 0 } child })
                bottom = Math.Max(top, bottom - child.Margin.Bottom);
            if (item.Topic is { } topic)
                context.DrawLine(PenFor(topic), new Point(RailInset, top), new Point(RailInset, bottom));
            // A topic's own rail starts just under its header's icon.
            else if (item is ChatTopicItem { IsExpanded: true } header)
                context.DrawLine(PenFor(header), new Point(RailInset, (top + bottom) / 2 + HeaderIconClearance), new Point(RailInset, bottom));
        }
    }

    IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    internal ChatTopicItem? RailAt(Point point) {
        if (point.X < 0 || Items.Count == 0 || _starts.Length != Items.Count + 1) return null;
        var index = IndexAt(point.Y);
        if (point.Y < _starts[index] || point.Y >= _starts[index + 1]) return null;
        return point.X < RailWidth && Items[index] is ChatItemViewModel item ? item.Topic : null;
    }

    internal void FoldFromRail(ChatTopicItem topic) {
        topic.ToggleCommand.Execute().Subscribe();
        // Folding from a rail far below the header would leave the reader past the fold.
        Dispatcher.UIThread.Post(() => {
            var index = ItemsControl?.Items.IndexOf(topic) ?? -1;
            if (index >= 0 && index < _starts.Length - 1 && _starts[index] < _viewport.Top) ScrollIntoView(index);
        }, DispatcherPriority.Background);
    }

    Row Realize(int index) {
        if (_realized.TryGetValue(index, out var existing)) return existing;
        var generator = ItemContainerGenerator!;
        var item = Items[index]!;
        var needsContainer = generator.NeedsContainer(item, index, out var key);
        if (needsContainer && IsMessage(item) && key is IDataTemplate template && TakeParked(template) is { } parked) {
            generator.PrepareItemContainer(parked, item, index);
            // Match the traversal order of newly realized containers.
            var position = Children.IndexOf(parked);
            if (position < Children.Count - 1) Children.Move(position, Children.Count - 1);
            parked.IsVisible = true;
            generator.ItemContainerPrepared(parked, item, index);
            var reused = new Row(item, parked, key, false);
            _realized.Add(index, reused);
            return reused;
        }
        Control? container = null;
        var pooled = needsContainer && key is not null && _pool.TryGetValue(key, out var pool) && pool.TryPop(out container);
        if (!pooled) container = needsContainer ? generator.CreateContainer(item, index, key) : (Control)item;
        container!.IsVisible = true;
        generator.PrepareItemContainer(container, item, index);
        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);
        var row = new Row(item, container, key, !needsContainer);
        _realized.Add(index, row);
        return row;
    }

    static bool IsMessage(object item) => item is UserTurnItem or AssistantTextItem;

    ChatRowContainer? TakeParked(IDataTemplate template) {
        if (_messagePool is not { } pool) return null;
        for (var i = pool.Count - 1; i >= 0; i--) {
            if (!ReferenceEquals(pool[i].Template, template)) continue;
            var parked = pool[i];
            pool.RemoveAt(i);
            return parked;
        }
        return null;
    }

    void ClearMessagePool() {
        if (_messagePool is not { } pool) return;
        _messagePool = null;
        foreach (var message in pool) RemoveInternalChild(message);
    }

    void Unrealize(int index, bool recycleMessage) {
        if (!_realized.Remove(index, out var row)) return;
        this.FindAncestorOfType<IScrollAnchorProvider>()?.UnregisterAnchorCandidate(row.Container);
        if (row.OwnContainer) {
            RemoveInternalChild(row.Container);
            return;
        }
        if (ItemsControl is { } owner && KeyboardNavigation.GetTabOnceActiveElement(owner) is Visual active
            && ContainsInput(row.Container, active))
            KeyboardNavigation.SetTabOnceActiveElement(owner, null);
        var canPark = recycleMessage && IsMessage(row.Item) && row.Container is ChatRowContainer
            && (_messagePool?.Count ?? 0) < MessagePoolLimit && !row.Container.IsKeyboardFocusWithin
            && !ContainsInput(row.Container, this.FindAncestorOfType<ChatTabView>()?.CapturedTranscriptElement);
        ItemContainerGenerator!.ClearItemContainer(row.Container);
        if (canPark) {
            row.Container.IsVisible = false;
            (_messagePool ??= []).Add((ChatRowContainer)row.Container);
            return;
        }
        if (row.RecycleKey is { } key) {
            row.Container.IsVisible = false;
            if (!_pool.TryGetValue(key, out var pool)) _pool.Add(key, pool = new Stack<Control>());
            pool.Push(row.Container);
        }
        RemoveInternalChild(row.Container);
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e) {
        var inserted = e is { Action: NotifyCollectionChangedAction.Add, NewStartingIndex: 0, NewItems.Count: var count }
            && count < items.Count && _realized.Count > 0 ? count : 0;
        var reading = inserted > 0 ? IndexAt(Math.Max(0, _viewport.Top)) : 0;
        var within = inserted > 0 ? _viewport.Top - _starts[reading] : 0;
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewStartingIndex != _starts.Length - 1) {
            if (e.Action == NotifyCollectionChangedAction.Reset) {
                foreach (var index in _realized.Keys.ToArray()) Unrealize(index, recycleMessage: false);
                ClearMessagePool();
                _measured.Clear();
            } else {
                var indices = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < items.Count; i++)
                    if (items[i] is { } item) indices[item] = i;
                var retained = new Dictionary<int, Row>();
                foreach (var entry in _realized.ToArray()) {
                    if (!indices.TryGetValue(entry.Value.Item, out var index)) Unrealize(entry.Key, recycleMessage: true);
                    else {
                        retained.Add(index, entry.Value);
                        if (index != entry.Key) ItemContainerGenerator!.ItemContainerIndexChanged(entry.Value.Container, entry.Key, index);
                    }
                }
                _realized.Clear();
                foreach (var entry in retained) _realized.Add(entry.Key, entry.Value);
                foreach (var item in _measured.Keys.Where(item => !indices.ContainsKey(item)).ToArray()) _measured.Remove(item);
            }
            _first = _last = -1;
        }
        RebuildStarts();
        // Rows inserted above the ones on screen move the viewport with them, so the reader keeps
        // reading the same row rather than whatever now sits at the old offset.
        if (inserted > 0 && reading + inserted < items.Count && this.FindAncestorOfType<ScrollViewer>() is { } scroll
            && items[reading + inserted] is { } readingItem) {
            var inset = _insertAnchor?.Inset ?? scroll.Offset.Y - _viewport.Top;
            var offset = _insertAnchor?.Offset ?? scroll.Offset.Y;
            var top = _starts[reading + inserted] + within;
            _shiftInFlight += top - _viewport.Top;
            _viewport = new Rect(_viewport.X, top, _viewport.Width, _viewport.Height);
            _insertAnchor = (readingItem, within, inset, offset);
        }
        InvalidateMeasure();
    }

    internal bool TryFocusRow(int index, Func<Control, Control?> focus) {
        if (_realized.TryGetValue(index, out var existing)) {
            if (focus(existing.Container) is not { } target) return false;
            target.BringIntoView();
            return true;
        }

        // Measure the candidate so its template controls exist before changing the viewport.
        var row = Realize(index);
        row.Container.Measure(new Size(RowWidth(row.Item, _lastWidth), double.PositiveInfinity));
        if (focus(row.Container) is null) {
            Unrealize(index, recycleMessage: true);
            return false;
        }
        ScrollIntoView(index);
        return true;
    }

    protected override Control? ScrollIntoView(int index) {
        if (index < 0 || index >= Items.Count) return null;
        // An explicit scroll outranks the correction a recent insertion still has pending. Until that
        // correction lands the viewport has moved and the offset has not, so the offset is set outright.
        var pending = _insertAnchor;
        _insertAnchor = null;
        ReadingLayoutAnchorPending = false;
        _shiftInFlight = 0;
        if (_starts.Length != Items.Count + 1) RebuildStarts();
        var row = Realize(index);
        InvalidateMeasure();
        if (this.FindAncestorOfType<ScrollViewer>() is { } scroll) {
            var inset = pending?.Inset ?? scroll.Offset.Y - _viewport.Top;
            var target = _starts[index] + inset;
            scroll.Offset = new Vector(scroll.Offset.X, target);
            // Past the extent the presenter has laid out, the offset was clamped; arrange applies it once the extent grows.
            if (scroll.Offset.Y < target - 0.5) _insertAnchor = (row.Item, 0, inset, scroll.Offset.Y);
        }
        return row.Container;
    }

    protected override Control? ContainerFromIndex(int index) =>
        _realized.TryGetValue(index, out var row) ? row.Container : null;
    protected override int IndexFromContainer(Control container) {
        foreach (var entry in _realized)
            if (ReferenceEquals(entry.Value.Container, container)) return entry.Key;
        return -1;
    }
    protected override IEnumerable<Control> GetRealizedContainers() => _realized.Values.Select(row => row.Container);
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) {
        // ItemsControl tries this before ChatTranscriptKeyboard can search without scrolling.
        if (direction is NavigationDirection.Up or NavigationDirection.Down) return null;
        if (Items.Count == 0 || from is null && direction is not NavigationDirection.First and not NavigationDirection.Last) return null;
        // Directional navigation starts at the focused control inside the row, not the row.
        var index = -1;
        for (var visual = from as Visual; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
            if (visual is Control control && IndexFromContainer(control) is var found and >= 0) { index = found; break; }
        int? next = direction switch {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Items.Count - 1,
            NavigationDirection.Previous => index - 1,
            NavigationDirection.Next => index + 1,
            NavigationDirection.Left or NavigationDirection.Right => index,
            _ => null,
        };
        if (next is not { } target) return null;
        if (target == index) return from;
        if (wrap) target = (target + Items.Count) % Items.Count;
        return ScrollIntoView(target);
    }
}
