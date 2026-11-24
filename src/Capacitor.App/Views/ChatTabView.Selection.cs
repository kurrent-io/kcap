using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class ChatTabView {
    internal static readonly StyledProperty<bool> TranscriptSelectionEnabledProperty =
        AvaloniaProperty.Register<ChatTabView, bool>(nameof(TranscriptSelectionEnabled));

    internal bool TranscriptSelectionEnabled => GetValue(TranscriptSelectionEnabledProperty);

    ChatTabViewModel? _selectionChat;
    ChatSelection? _range;
    (IPointer Pointer, Point Position, ChatSelection.Point Anchor)? _rangePress;
    IPointer? _rangePointer;
    long _rangeLimit;

    static ChatTabView() {
        TranscriptSelectionEnabledProperty.Changed.AddClassHandler<ChatTabView>((view, _) => view.ObserveTranscriptSelection());
    }

    void ObserveTranscriptSelection() {
        var chat = TranscriptSelectionEnabled && this.IsAttachedToVisualTree() ? DataContext as ChatTabViewModel : null;
        if (ReferenceEquals(chat, _selectionChat)) return;
        DetachTranscriptSelection();
        if (chat is null) return;

        _selectionChat = chat;
        _range = chat.Selection;
        _range.Labels = GetValue(MarkdownView.AgentLinksProperty);
        _range.Changed += OnRangeChanged;
        chat.Items.CollectionChanged += OnSelectionItemsChanged;
        ChatItems.AddHandler(PointerPressedEvent, OnTranscriptPress, RoutingStrategies.Tunnel);
        ChatItems.AddHandler(PointerMovedEvent, OnTranscriptMove, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(PointerReleasedEvent, OnTranscriptRelease, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(KeyDownEvent, OnTranscriptKey, RoutingStrategies.Tunnel);
        JumpToLatest.AddHandler(KeyDownEvent, OnTranscriptKey, RoutingStrategies.Tunnel);
        ChatItems.AddHandler(PointerCaptureLostEvent, OnTranscriptCaptureLost);
        LayoutUpdated += OnSelectionLayout;
        ChatItems.Focusable = true;
        AutomationProperties.SetHelpText(ChatItems, "Hold Shift and use the arrow keys to extend the transcript selection.");
        SelectionOverlay.Paint = PaintTranscriptSelection;
        SelectionOverlay.IsVisible = true;
        RefreshJumpToLatest();
    }

    void DetachTranscriptSelection() {
        if (_selectionChat is not { } chat || _range is not { } range) return;
        EndTranscriptDrag();
        range.Changed -= OnRangeChanged;
        range.Clear();
        range.Labels = null;
        chat.Items.CollectionChanged -= OnSelectionItemsChanged;
        _selectionChat = null;
        _range = null;
        LayoutUpdated -= OnSelectionLayout;
        ChatItems.RemoveHandler(PointerPressedEvent, OnTranscriptPress);
        ChatItems.RemoveHandler(PointerMovedEvent, OnTranscriptMove);
        ChatItems.RemoveHandler(PointerReleasedEvent, OnTranscriptRelease);
        ChatItems.RemoveHandler(KeyDownEvent, OnTranscriptKey);
        JumpToLatest.RemoveHandler(KeyDownEvent, OnTranscriptKey);
        ChatItems.RemoveHandler(PointerCaptureLostEvent, OnTranscriptCaptureLost);
        ChatItems.Focusable = false;
        AutomationProperties.SetHelpText(ChatItems, null);
        SelectionOverlay.Paint = null;
        SelectionOverlay.IsVisible = false;
        if (JumpToLatest.IsFocused || ChatItems.IsFocused) ComposerInput.Focus();
        JumpToLatest.IsVisible = false;
    }

    void OnSelectionItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (_range is { Focus: not null } range && e.Action is not NotifyCollectionChangedAction.Add)
            range.TryRange(out _, out _);
    }

    void OnSelectionLayout(object? sender, EventArgs e) {
        _range?.CheckFeed();
        if (_range?.HasRange == true) SelectionOverlay.InvalidateVisual();
        RefreshJumpToLatest();
    }

    void OnRangeChanged() {
        if (_range?.Focus is null) EndTranscriptDrag();
        else ChatItems.Focusable = true;
        if (HasTranscriptSelection) {
            FollowTail = false;
            CancelSmoothScroll();
        }
        SelectionOverlay.InvalidateVisual();
        RefreshJumpToLatest();
    }

    internal bool HasTranscriptSelection => _range?.HasRange == true;

    internal bool HasTranscriptSelectionIn(ChatItemViewModel row) => _range?.IsWithin(row) == true;

    internal string? TranscriptSelectionIn(ChatItemViewModel row) =>
        _range?.IsWithin(row) == true ? _range.CopyText() : null;

    internal void ClearTranscriptSelection() {
        EndTranscriptDrag();
        _range?.Clear();
    }

    void RefreshJumpToLatest() {
        if (_range is null) return;
        var show = !FollowTail && ChatItems.ItemCount > 0;
        var restoreFocus = !show && JumpToLatest.IsFocused;
        if (restoreFocus) {
            ChatItems.Focusable = true;
            ChatItems.Focus(NavigationMethod.Tab);
        }
        JumpToLatest.IsVisible = show;
        if (restoreFocus) RestoreLatestActionAfterLayout();
    }

    void OnJumpToLatest(object? sender, RoutedEventArgs e) {
        if (_range is null) return;
        ChatItems.Focusable = true;
        ChatItems.Focus(NavigationMethod.Tab);
        ResumeFollowing();
        RestoreLatestActionAfterLayout();
    }

    void RestoreLatestActionAfterLayout() {
        Dispatcher.UIThread.Post(() => {
            if (ChatItems.IsFocused) FocusReadingAction(ChatItems.ItemCount - 1);
        }, DispatcherPriority.Loaded);
    }

    void FocusReadingAction(int preferredIndex) {
        if (_scroll is not { } scroll) return;
        var viewport = new Rect(scroll.Viewport);
        var visible = ChatItems.GetRealizedContainers()
            .Where(row => ChatSelectionOverlay.BoundsIn(row, scroll) is { } bounds && bounds.Intersects(viewport))
            .OrderBy(row => Math.Abs(ChatItems.IndexFromContainer(row) - preferredIndex));
        foreach (var row in visible) {
            if (ChatTranscriptKeyboard.FocusFirst(row) is null) continue;
            ChatItems.Focusable = false;
            return;
        }
        if (JumpToLatest.IsEffectivelyVisible) JumpToLatest.Focus(NavigationMethod.Tab);
        else ComposerInput.Focus(NavigationMethod.Tab);
        ChatItems.Focusable = false;
    }

    void OnTranscriptPress(object? sender, PointerPressedEventArgs e) {
        if (_range is null || e.Pointer.Type == PointerType.Touch
            || e.GetCurrentPoint(ChatItems).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        if (e.Source is not Visual source || IsInteractive(source, allowSummary: true)) return;
        ClearTranscriptSelection();
        if (e.ClickCount != 1) return;
        EnsureChatScroll();
        if (_scroll is null) return;
        var point = e.GetPosition(_scroll);
        var container = source.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => ChatItems.IndexFromContainer(c) >= 0);
        if (container?.DataContext is not ChatItemViewModel { SelectionKey: not null } row) return;
        if (ChatSelectionOverlay.Hit(container, row, _range.Read(row), _scroll, point, ChatItems.IndexFromContainer(container)) is { } anchor)
            _rangePress = (e.Pointer, point, anchor);
        _rangeLimit = _range.LatestLine;
    }

    static bool IsInteractive(Visual source, bool allowSummary = false) => source.GetSelfAndVisualAncestors().Any(c =>
        c is TextBox or ScrollBar or Thumb or MarkdownImage or ToolImagePreview
        || c is TextBlock { Name: "ToolRowDetail", DataContext: ToolCallItem { CanOpenFile: true } }
        || c is Button button && (!allowSummary || !button.Classes.Contains("toolSummary")));

    void OnTranscriptMove(object? sender, PointerEventArgs e) {
        if (_range is not { } range || _scroll is null || !e.GetCurrentPoint(ChatItems).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(_scroll);
        if (_rangePointer != e.Pointer) {
            if (_rangePress is not { } press || press.Pointer != e.Pointer || new Vector(point.X - press.Position.X, point.Y - press.Position.Y).SquaredLength < 16) return;
            if (!range.CanStart(press.Anchor)) {
                EndTranscriptDrag();
                return;
            }
            _rangePointer = e.Pointer;
            _rangePress = null;
            range.Set(press.Anchor, press.Anchor);
            foreach (var markdown in _selectedMarkdown.ToArray()) markdown.ClearSelection();
            if (e.Pointer.Captured is Visual source
                && source.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is { } plain)
                plain.ClearSelection();
            // The stable list owns capture and focus so the starting row can recycle.
            e.Pointer.Capture(ChatItems);
            ChatItems.Focus();
            _primaryPointer = e.Pointer;
            DropToggleAnchor();
            FollowTail = false;
            _dragView = null;
            _dragSelecting = true;
        }
        ExtendTranscriptDrag(point);
        _dragInScroll = point;
        QueueEdgeScroll();
        e.Handled = true;
    }

    void ExtendTranscriptDrag(Point point) {
        if (_range is not { } range || _scroll is null) return;
        // Keep hit testing inside the viewport while edge scrolling brings in the next rows.
        point = new Point(point.X, Math.Clamp(point.Y, 0, Math.Max(0, _scroll.Viewport.Height - 1)));
        if (NearestSelectionRow(_scroll, point.Y, _rangeLimit) is not { } container
            || container.DataContext is not ChatItemViewModel row) return;
        var text = range.TextOf(row);
        if (ChatSelectionOverlay.Hit(container, row, text, _scroll, point, ChatItems.IndexFromContainer(container)) is { } focus)
            range.Extend(focus);
    }

    Control? NearestSelectionRow(Visual relativeTo, double y, long latestLine = long.MaxValue) {
        Control? best = null;
        var distance = double.PositiveInfinity;
        foreach (var container in ChatItems.GetRealizedContainers()) {
            if (!container.IsEffectivelyVisible || container.DataContext is not ChatItemViewModel { SelectionKey.Line: var line } || line > latestLine
                || ChatSelectionOverlay.BoundsIn(container, relativeTo) is not { } bounds || bounds.Height <= 1) continue;
            var gap = Math.Abs(y - Math.Clamp(y, bounds.Top, bounds.Bottom));
            if (gap >= distance) continue;
            (best, distance) = (container, gap);
        }
        return best;
    }

    void OnTranscriptRelease(object? sender, PointerReleasedEventArgs e) {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (_rangePointer == e.Pointer) {
            if (_scroll is not null) ExtendTranscriptDrag(e.GetPosition(_scroll));
            EndTranscriptDrag();
            // The jump button can take focus when it appears during a captured drag.
            if (HasTranscriptSelection && JumpToLatest.IsFocused) ChatItems.Focus(NavigationMethod.Pointer);
            e.Handled = true;
            return;
        }
        if (_rangePress is { } press && press.Pointer == e.Pointer && _range is { } range
            && e.Source is Visual source && !IsInteractive(source) && range.CanStart(press.Anchor)) {
            range.Set(press.Anchor, press.Anchor);
        }
        EndTranscriptDrag();
    }

    void OnTranscriptCaptureLost(object? sender, PointerCaptureLostEventArgs e) {
        if (ReferenceEquals(e.Source, ChatItems) && ReferenceEquals(e.Pointer, _rangePointer)) EndTranscriptDrag();
    }

    void EndTranscriptDrag() {
        _rangePress = null;
        if (_rangePointer is not { } pointer) return;
        _rangePointer = null;
        _dragSelecting = false;
        if (ReferenceEquals(pointer.Captured, ChatItems)) pointer.Capture(null);
    }

    void OnTranscriptKey(object? sender, KeyEventArgs e) {
        if (_range is not { } range || e.Source is not Visual source || OriginatesFromTextBox(e)) return;
        // The jump button can receive focus when a keyboard range makes it appear.
        var onJump = ReferenceEquals(sender, JumpToLatest);
        if (onJump ? !range.HasRange : IsInteractive(source) && e.KeyModifiers != KeyModifiers.Shift) return;
        var hotkeys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (hotkeys?.Copy.Any(g => g.Matches(e)) == true && TranscriptSelectionCopy() is not null) {
            e.Handled = true;
            return;
        }
        if (e is { Key: Key.Escape, KeyModifiers: KeyModifiers.None } && range.Focus is { } endpoint) {
            e.Handled = range.HasRange || _rangePointer is not null;
            var endIndex = range.IndexOf(endpoint);
            ClearTranscriptSelection();
            if (ChatItems.IsFocused || onJump) FocusReadingAction(endIndex);
            return;
        }
        if (e.KeyModifiers != KeyModifiers.Shift) {
            // Restore a row action so the bubbling Up/Down handler can navigate from it.
            if (ChatItems.IsFocused && e.KeyModifiers == KeyModifiers.None && e.Key is Key.Up or Key.Down) {
                var readingIndex = range.Focus is { } cursor ? range.IndexOf(cursor) : ChatItems.ItemCount - 1;
                ClearTranscriptSelection();
                FocusReadingAction(readingIndex);
            }
            return;
        }
        var direction = e.Key switch {
            Key.Left or Key.Up => -1,
            Key.Right or Key.Down => 1,
            _ => 0,
        };
        if (direction == 0) return;
        var message = e.Key is Key.Up or Key.Down;
        EnsureChatScroll();
        if (_scroll is null) return;
        if (range.Focus is null) {
            var container = source.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => ChatItems.IndexFromContainer(c) >= 0)
                ?? NearestSelectionRow(_scroll, Math.Max(0, _scroll.Viewport.Height - 1));
            if (container?.DataContext is not ChatItemViewModel { SelectionKey: not null } row
                || range.Read(row) is not { Length: > 0 } text) return;
            var at = ChatSelection.Point.At(row, text, direction < 0 ? text.Length : 0, ChatItems.IndexFromContainer(container));
            range.Set(at, at);
        }
        if (!range.Move(direction, message) || range.Focus is not { } focus) return;
        e.Handled = true;
        ChatItems.Focus(NavigationMethod.Directional);
        FollowTail = false;
        var index = range.IndexOf(focus);
        if (index >= 0) RevealSelectionFocus(index, focus, _scroll);
        RefreshJumpToLatest();
    }

    void RevealSelectionFocus(int index, ChatSelection.Point focus, ScrollViewer scroll) {
        if (ChatItems.ContainerFromIndex(index) is not { } row
            || ChatSelectionOverlay.BoundsIn(row, scroll) is not { } bounds
            || bounds.Bottom <= 0 || bounds.Top >= scroll.Viewport.Height) {
            ChatItems.ScrollIntoView(index);
            return;
        }
        if (ChatSelectionOverlay.Caret(row, focus, scroll) is not { } caret) return;
        var delta = caret.Top < 0 ? caret.Top : Math.Max(0, caret.Bottom - scroll.Viewport.Height);
        if (Math.Abs(delta) > 0.5) scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + delta);
    }

    internal Task? TranscriptSelectionCopy() {
        if (_range is not { HasRange: true } range || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return null;
        var request = range.Revision;
        return MarkdownClipboard.InOrder(async () => {
            if (!IsEffectivelyVisible || !ReferenceEquals(range, _range) || range.Revision != request
                || range.CopyText() is not { } text) return;
            try {
                await clipboard.SetTextAsync(text);
                LiveAnnouncer.Polite(ChatItems, "Copied");
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: transcript copy failed: {ex.Message}");
                LiveAnnouncer.Polite(ChatItems, "Copy failed");
            }
        });
    }

    void PaintTranscriptSelection(DrawingContext context) {
        if (_range is not { HasRange: true } range || !range.TryRange(out var start, out var end, validateText: false)
            || SelectionOverlay.Foreground is not { } brush || _scroll is null) return;
        var viewport = new Rect(0, 0, _scroll.Viewport.Width, _scroll.Viewport.Height);
        using var clip = context.PushClip(viewport);
        foreach (var container in ChatItems.GetRealizedContainers()) {
            var index = ChatItems.IndexFromContainer(container);
            if (index < start.Index || index > end.Index || container.DataContext is not ChatItemViewModel row
                || ChatSelectionOverlay.BoundsIn(container, SelectionOverlay) is not { } bounds || !bounds.Intersects(viewport)) continue;
            var text = range.TextOf(row);
            var from = index == start.Index ? start.Point.Offset : 0;
            var to = index == end.Index ? end.Point.Offset : text.Length;
            foreach (var run in ChatSelectionOverlay.Runs(container, text)) {
                var begin = Math.Max(from, run.Start);
                var finish = Math.Min(to, run.Start + run.Length);
                if (begin >= finish || run.Block.TranslatePoint(new Point(run.Block.Padding.Left, run.Block.Padding.Top), SelectionOverlay) is not { } origin)
                    continue;
                var native = run.ToNative(begin);
                foreach (var rect in run.Block.TextLayout.HitTestTextRange(native, run.ToNative(finish) - native))
                    context.FillRectangle(brush, rect.Translate(origin));
            }
        }
    }
}
