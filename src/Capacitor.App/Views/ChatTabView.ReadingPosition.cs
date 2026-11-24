using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using ReactiveUI.Reactive;

namespace Capacitor.App.Views;

public partial class ChatTabView {
    ChatTabViewModel? _readingChat;
    IDisposable? _readingSubjects;
    string? _readingSubject;
    string? _readingKey;
    int _readingVersion;
    bool _readingPending;
    bool _readingApplying;
    bool _readingCaptureQueued;
    bool _readingNavigated;
    bool _keepReadingFallback;
    bool _holdRestoredPosition;
    ChatItemViewModel? _capturedReadingRow;
    string? _capturedReadingOutput;
    string? _capturedReadingProof;

    void ObserveReadingPosition() {
        var chat = TranscriptSelectionEnabled && this.IsAttachedToVisualTree()
            ? DataContext as ChatTabViewModel : null;
        if (chat?.ReadingPositions is null) chat = null;
        if (ReferenceEquals(chat, _readingChat)) return;
        StopReadingPosition();
        if (chat?.ReadingPositions is not { } positions) return;

        _readingChat = chat;
        chat.ReadingFeedChanging += ReadingFeedChanging;
        chat.ReadingRowsReady += ReadingRowsReady;
        chat.ReadingPositionInvalidated += ReadingPositionInvalidated;
        _readingSubjects = positions.Subjects.ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(subject => {
                if (!ReferenceEquals(chat, _readingChat) || _readingSubject == subject) return;
                // Rows held from another account cannot seed or save a remote position.
                if (_readingSubject is not null || _readingKey is not null) ReadingFeedChanging(capture: false);
                _readingSubject = subject;
                if (chat.ReadingPositionIsInvalid) ReadingPositionInvalidated();
                else ReadingRowsReady();
            });
    }

    void StopReadingPosition() {
        if (_readingChat is not { } chat) return;
        ReadingFeedChanging();
        chat.ReadingFeedChanging -= ReadingFeedChanging;
        chat.ReadingRowsReady -= ReadingRowsReady;
        chat.ReadingPositionInvalidated -= ReadingPositionInvalidated;
        _readingSubjects?.Dispose();
        _readingSubjects = null;
        _readingChat = null;
        _readingSubject = null;
    }

    void ReadingFeedChanging() => ReadingFeedChanging(capture: true);

    void ReadingFeedChanging(bool capture) {
        if (_readingChat?.ReadingPositions is not { } positions) return;
        if (capture) CaptureReadingPosition();
        CancelReadingRestore();
        _readingKey = null;
        _readingNavigated = false;
        _capturedReadingRow = null;
        _capturedReadingOutput = null;
        _capturedReadingProof = null;
        _ = positions.FlushAsync();
    }

    void ReadingPositionInvalidated() {
        ReadingFeedChanging(capture: false);
        FollowTail = true;
    }

    void ReadingRowsReady() {
        if (_readingChat is not { ReadingFeedReady: true, ReadingFeedKey: { } feed, ReadingPositions: { } positions } chat
            || string.IsNullOrEmpty(_readingSubject)) return;
        if (!chat.AllowsReadingSubject(_readingSubject, admit: true)) return;

        var key = chat.ReadingScope(_readingSubject, feed);
        if (key == _readingKey) return;
        LayoutUpdated -= RetryReadingRestoreAfterLayout;
        _readingKey = key;
        if (_readingNavigated) {
            QueueReadingCapture();
            return;
        }
        _readingPending = true;
        _ = RestoreReadingPositionAsync(positions, key, ++_readingVersion);
    }

    async Task RestoreReadingPositionAsync(ChatReadingPositions positions, string key, int version) {
        var saved = await positions.ReadAsync(key);
        // The feed can finish inside an Avalonia binding or a layout pass.
        await Dispatcher.UIThread.InvokeAsync(() => RestoreReadingPosition(saved, version), DispatcherPriority.Loaded);
    }

    void RestoreReadingPosition(ChatReadingPosition? saved, int version) {
        if (!_readingPending || version != _readingVersion || _readingChat is not { ReadingFeedReady: true } chat) return;
        if (!IsEffectivelyVisible) {
            // The chat tree stays attached when a sibling tab covers it.
            _readingPending = false;
            _readingKey = null;
            LayoutUpdated += RetryReadingRestoreAfterLayout;
            return;
        }

        if (PrimaryDown || ListTextBoxOwnsFocus() || SelectionHeld()) {
            ReadingNavigation();
            return;
        }

        _readingApplying = true;
        try {
            var (row, exact) = saved is null || saved.Source is { } source && chat.ReadingSourceStamp is { } current && source != current
                ? default : ChatReadingAnchor.Find(chat.Topics, saved);
            if (row is not null && chat.Topics.MakeVisible(row)) {
                DropToggleAnchor();
                CancelSmoothScroll();
                _thumbDragging = false;
                _thumbFraction = null;
                FollowTail = false;
                _holdRestoredPosition = true;
                _keepReadingFallback = !exact;
                EnsureChatScroll();
                var index = ChatItems.Items.IndexOf(row);
                if (_scroll is not null && index >= 0) {
                    RevealTranscriptRow(index);
                    if (ChatItems.ItemsPanelRoot is ChatVirtualizingPanel panel)
                        panel.ApplyReadingOffset(index, exact ? saved!.Within : 0, _scroll);
                    return;
                }
            }
            _holdRestoredPosition = false;
            FollowTail = true;
            EnsureChatScroll();
            _scroll?.ScrollToEnd();
            if (saved?.Key is not null && _readingKey is { } stale) chat.ReadingPositions!.Remember(stale, null);
        } finally {
            _readingPending = false;
            _readingApplying = false;
        }
    }

    void CancelReadingRestore() {
        LayoutUpdated -= RetryReadingRestoreAfterLayout;
        _readingVersion++;
        _readingPending = false;
        _keepReadingFallback = false;
        _holdRestoredPosition = false;
    }

    void OnReadingNavigation(RoutedEventArgs e) {
        if (e is PointerWheelEventArgs wheel && DefersToInnerScroll(wheel)) return;
        var readsEarlier = e is PointerWheelEventArgs { Delta.Y: > 0 } or ScrollGestureEventArgs { Delta.Y: < 0 }
            or KeyEventArgs { Key: Key.PageUp or Key.Home or Key.Up };
        if (readsEarlier && _scroll is { Offset.Y: <= BottomTolerance } && _readingChat is { ReadingFeedReady: true } reading)
            reading.RequestOlder();

        if (readsEarlier || e is PointerWheelEventArgs or ScrollGestureEventArgs or KeyEventArgs { Key: Key.PageDown or Key.End or Key.Down }
            || e.RoutedEvent == Thumb.DragDeltaEvent || e is PointerPressedEventArgs { Source: Visual source }
                && source.GetSelfAndVisualAncestors().OfType<ScrollBar>().FirstOrDefault() is { } bar && ReferenceEquals(bar.TemplatedParent, _scroll))
            ReadingNavigation();
    }

    void ReadingNavigation() {
        if (_readingApplying) return;
        _readingNavigated = true;
        if (_readingPending || _keepReadingFallback || _holdRestoredPosition) CancelReadingRestore();
        QueueReadingCapture();
    }

    void QueueReadingCapture() {
        if (_readingKey is null || _readingCaptureQueued || _readingPending || _readingApplying || _keepReadingFallback) return;
        _readingCaptureQueued = true;
        Dispatcher.UIThread.Post(() => {
            _readingCaptureQueued = false;
            CaptureReadingPosition();
        }, DispatcherPriority.Loaded);
    }

    void CaptureReadingPosition() {
        if (_readingPending || _readingApplying || _keepReadingFallback || _readingKey is not { } key
            || _readingChat is not { ReadingFeedReady: true, ReadingPositions: { } positions } chat
            || !ReferenceEquals(ChatItems.ItemsSource, chat.Items)
            || !IsEffectivelyVisible
            || ChatItems.ItemsPanelRoot is not ChatVirtualizingPanel { ReadingLayoutAnchorPending: false } panel) return;
        if (FollowTail && !SelectionHeld()) {
            positions.Remember(key, ChatReadingPosition.Live);
            return;
        }
        if (panel.GetReadingAnchor() is not { } anchor) return;
        var output = (anchor.Row as ShellCommandItem) is { HasCommand: false } shell ? shell.Output : null;
        if (!ReferenceEquals(anchor.Row, _capturedReadingRow) || !ReferenceEquals(output, _capturedReadingOutput)) {
            _capturedReadingRow = anchor.Row;
            _capturedReadingOutput = output;
            _capturedReadingProof = ChatReadingAnchor.Proof(anchor.Row);
        }
        positions.Remember(key, new ChatReadingPosition(anchor.Key, Math.Round(anchor.Within, 1), _capturedReadingProof, chat.ReadingSourceStamp));
    }

    void RetryReadingRestoreAfterLayout(object? sender, EventArgs e) {
        if (!IsEffectivelyVisible) return;
        LayoutUpdated -= RetryReadingRestoreAfterLayout;
        ReadingRowsReady();
    }
}
