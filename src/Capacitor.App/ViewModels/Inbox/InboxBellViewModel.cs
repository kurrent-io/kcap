using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Collections;
using Capacitor.App.Services.Inbox;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Inbox;

/// <summary>
/// The rail footer's notification bell: the signed-in user's server inbox, as the web bell shows it. Shown only where
/// the server offers notifications to this user and serves the inbox routes. The badge reads the unread count alone;
/// the list is read only while the panel is open. Pushes and reconnects coalesce: while a read runs, any number of
/// further nudges cost one more read after it.
/// </summary>
public sealed class InboxBellViewModel : ReactiveObject, IDisposable {
    public const int PageSize = 50;

    readonly IInboxApi _api;
    readonly TimeProvider _time;
    readonly Action<string> _open;
    readonly CompositeDisposable _disposables = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly AvaloniaList<InboxItemViewModel> _items = new();
    // Rows dismissed here, by the version dismissed: kept off the list until a page no longer carries them. A newer
    // version shows the row again, as the server refuses a dismiss of an older one without an error.
    readonly Dictionary<string, long> _dismissing = new(StringComparer.Ordinal);
    // A card's answer sentence, kept across the re-reads that replace the card.
    readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);
    bool _connected;
    // Advances on every connect and disconnect, so a read begun under one connection never lands under the next.
    int _epoch;
    bool _refreshRunning;
    bool _refreshQueued;
    bool _admitQueued;
    bool _admitRunning;
    bool _disposed;

    public IAvaloniaReadOnlyList<InboxItemViewModel> Items => _items;

    public bool IsVisible {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool PassesOffered {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public int UnreadCount {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasUnread));
            this.RaisePropertyChanged(nameof(BadgeText));
            this.RaisePropertyChanged(nameof(UnreadText));
            this.RaisePropertyChanged(nameof(AccessibleName));
        }
    }

    public bool    HasUnread      => UnreadCount > 0;
    public string  BadgeText      => UnreadCount > 99 ? "99+" : UnreadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string  UnreadText     => $"{UnreadCount} unread";
    public string  AccessibleName => UnreadCount > 0 ? $"Notifications, {UnreadCount} unread" : "Notifications";

    /// <summary>Set by the view as the panel opens and closes; opening reads the list.</summary>
    public bool IsOpen {
        get;
        set {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsLoading));
            if (!value) IsConfirmingDeleteAll = false;
            if (value) Refresh();
        }
    }

    /// <summary>True from the panel opening until the first list read succeeds: an honest spinner while reads keep
    /// failing beats an "all caught up" that may be wrong.</summary>
    public bool IsLoading => IsOpen && !ListLoaded;

    public bool ListLoaded {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsLoading));
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    public bool HasItems => _items.Count > 0;
    public bool IsEmpty  => ListLoaded && _items.Count == 0;

    public bool IsConfirmingDeleteAll {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> MarkAllReadCommand    { get; }
    public ReactiveCommand<Unit, Unit> AskDeleteAllCommand   { get; }
    public ReactiveCommand<Unit, Unit> CancelDeleteAllCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteAllCommand      { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand   { get; }

    public InboxBellViewModel(IInboxApi api, IObservable<ServerLaneStatus> status, IObservable<Unit> changed,
            Action<string> open, Action openSettings, TimeProvider? time = null) {
        _api  = api;
        _open = open;
        _time = time ?? TimeProvider.System;
        MarkAllReadCommand     = ReactiveCommand.CreateFromTask(MarkAllReadAsync);
        AskDeleteAllCommand    = ReactiveCommand.Create(() => { IsConfirmingDeleteAll = true; });
        CancelDeleteAllCommand = ReactiveCommand.Create(() => { IsConfirmingDeleteAll = false; });
        DeleteAllCommand       = ReactiveCommand.CreateFromTask(DeleteAllAsync);
        OpenSettingsCommand    = ReactiveCommand.Create(() => { IsOpen = false; openSettings(); });

        status.Select(s => s.State == ServerLaneState.Connected).DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(OnConnectedChanged).DisposeWith(_disposables);
        changed.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => Refresh()).DisposeWith(_disposables);
    }

    void OnConnectedChanged(bool connected) {
        _connected = connected;
        _epoch++;
        if (connected) {
            _ = AdmitAsync(_epoch);
            return;
        }
        _admitQueued = false;
        Hide();
    }

    async Task AdmitAsync(int epoch) {
        _admitRunning = true;
        try {
            await AdmitOnceAsync(epoch);
        } finally {
            _admitRunning = false;
        }
    }

    async Task AdmitOnceAsync(int epoch) {
        var surfaces = await Guard(_api.SurfacesAsync(_lifetime.Token));
        if (_disposed || epoch != _epoch || surfaces is null) return;
        if (surfaces is not { Kind: InboxReadKind.Ready, Value.Notifications: true }) {
            Hide();
            return;
        }
        PassesOffered = surfaces.Value.Passes;
        await RefreshAsync(admitting: true);
    }

    void Hide() {
        IsVisible = false;
        IsOpen = false;
        UnreadCount = 0;
        ListLoaded = false;
        _notes.Clear();
        _dismissing.Clear();
        Replace([]);
    }

    // A push to a hidden bell retries admission, so one failed first read does not hide it for the rest of the connection.
    public void Refresh() {
        if (_disposed || !_connected) return;
        if (IsVisible) _ = RefreshAsync(admitting: false);
        else if (!_admitRunning) _ = AdmitAsync(_epoch);
    }

    // An admission queued behind a running read still runs once that read ends, even though the bell is hidden meanwhile.
    async Task RefreshAsync(bool admitting) {
        if (admitting) _admitQueued = true;
        else if (!IsVisible) return;
        if (_refreshRunning) {
            _refreshQueued = true;
            return;
        }
        _refreshRunning = true;
        try {
            do {
                _refreshQueued = false;
                var admit = _admitQueued;
                _admitQueued = false;
                if (IsOpen) await ReadListAsync(admit, _epoch);
                else await ReadCountAsync(admit, _epoch);
            } while (!_disposed && (_admitQueued || (_refreshQueued && IsVisible)));
        } finally {
            _refreshRunning = false;
        }
    }

    async Task ReadCountAsync(bool admitting, int epoch) {
        var read = await Guard(_api.UnreadCountAsync(_lifetime.Token));
        if (_disposed || epoch != _epoch || read is null) return;
        switch (read.Kind) {
            case InboxReadKind.Ready:
                UnreadCount = read.Value!.Count;
                if (admitting) IsVisible = true;
                break;
            case InboxReadKind.Unavailable or InboxReadKind.SignedOut:
                Hide();
                break;
        }
    }

    async Task ReadListAsync(bool admitting, int epoch) {
        var read = await Guard(_api.InboxAsync(PageSize, _lifetime.Token));
        if (_disposed || epoch != _epoch || read is null) return;
        switch (read.Kind) {
            case InboxReadKind.Ready:
                var page = read.Value!;
                var hidden = page.Items.Where(i => _dismissing.TryGetValue(i.LogicalId, out var v) && i.Version <= v).ToList();
                foreach (var id in _dismissing.Keys.Except(hidden.Select(i => i.LogicalId)).ToList()) _dismissing.Remove(id);
                var now = _time.GetUtcNow();
                var shown = page.Items.Except(hidden).ToList();
                foreach (var id in _notes.Keys.Except(shown.Select(i => i.LogicalId)).ToList()) _notes.Remove(id);
                var previous = _items.ToDictionary(i => i.LogicalId, StringComparer.Ordinal);
                Replace(shown.Select(i => Card(i, now, previous.GetValueOrDefault(i.LogicalId))).ToList());
                UnreadCount = Math.Max(0, page.UnreadCount - hidden.Count(i => i.IsUnread));
                ListLoaded = true;
                if (admitting) IsVisible = true;
                break;
            case InboxReadKind.Unavailable or InboxReadKind.SignedOut:
                Hide();
                break;
        }
    }

    // A re-read replaces every card; the one it replaces hands over what the person has typed and the last answer's sentence.
    InboxItemViewModel Card(NotificationItemDto dto, DateTimeOffset now, InboxItemViewModel? previous) {
        var card = new InboxItemViewModel(dto, now, _api.Resolve, MarkReadAsync, DismissAsync, AnswerAsync, Open);
        card.Carry(_notes.GetValueOrDefault(dto.LogicalId), previous is { IsAnswering: false } ? previous.ReplyText : null);
        return card;
    }

    async Task MarkReadAsync(InboxItemViewModel item) {
        await Guard(_api.MarkReadAsync(item.LogicalId, item.Version, _lifetime.Token));
        Refresh();
    }

    async Task DismissAsync(InboxItemViewModel item) {
        if (_dismissing.TryAdd(item.LogicalId, item.Version)) {
            _items.Remove(item);
            this.RaisePropertyChanged(nameof(HasItems));
            this.RaisePropertyChanged(nameof(IsEmpty));
            if (item.IsUnread) UnreadCount = Math.Max(0, UnreadCount - 1);
        }
        if (await Guard(_api.DismissAsync(item.LogicalId, item.Version, _lifetime.Token)) is not true) _dismissing.Remove(item.LogicalId);
        Refresh();
    }

    async Task<InboxAnswerOutcome> AnswerAsync(InboxItemViewModel item, string answer, string? text) {
        const string couldNotSend = "Couldn't send that answer. Try again, or answer it in kcap";
        var epoch = _epoch;
        var read = await Guard(_api.AnswerAsync(item.LogicalId, answer, text, _lifetime.Token));
        var answered = read is { Kind: InboxReadKind.Ready, Value: { } outcome } ? new InboxAnswerOutcome(outcome.Accepted, outcome.Message)
            : new InboxAnswerOutcome(false, couldNotSend);
        var note = answered.Note;
        if (_disposed || epoch != _epoch) return answered;
        _notes[item.LogicalId] = note;
        Refresh();
        return answered;
    }

    async Task MarkAllReadAsync() {
        await Guard(_api.MarkAllReadAsync(_lifetime.Token));
        Refresh();
    }

    async Task DeleteAllAsync() {
        IsConfirmingDeleteAll = false;
        await Guard(_api.DismissAllAsync(_lifetime.Token));
        Refresh();
    }

    void Open(string url) {
        IsOpen = false;
        _open(url);
    }

    void Replace(IReadOnlyList<InboxItemViewModel> next) {
        _items.Clear();
        _items.AddRange(next);
        this.RaisePropertyChanged(nameof(HasItems));
        this.RaisePropertyChanged(nameof(IsEmpty));
    }

    // Disposal cancels the lifetime token, which ends any read in flight; null means it ended that way.
    async Task<T?> Guard<T>(Task<T> read) {
        try {
            return await read;
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            return default;
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _disposables.Dispose();
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Dispose();
    }
}
