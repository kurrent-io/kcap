using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Collections;
using Capacitor.App.Services.Inbox;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Inbox;

/// <summary>The notification settings the web bell's dialog offers: Slack delivery, each type's In-app and Slack
/// switches, and the pass controls where passes are offered. Every write is followed by a re-read, so the switches
/// always show what the server holds.</summary>
public sealed class InboxSettingsViewModel : ReactiveObject, IDisposable {
    readonly IInboxApi _api;
    readonly CompositeDisposable _disposables = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly AvaloniaList<InboxTypeRowViewModel> _types = new();
    readonly AvaloniaList<InboxMutedSenderViewModel> _muted = new();
    // Every reload and every write advances it, so a read begun before a write never lands over what the write set.
    int _generation;
    int _mutations;
    bool _connected = true;
    string? _subject;
    // Per type, the switches to send next: a write in flight is followed by only the newest pair, so the server ends on
    // the person's last choice whatever order the requests would have completed in.
    readonly Dictionary<string, (bool Enabled, bool SlackEnabled)> _pendingWrites = new(StringComparer.Ordinal);
    readonly HashSet<string> _writing = new(StringComparer.Ordinal);
    bool _disposed;

    public IAvaloniaReadOnlyList<InboxTypeRowViewModel> Types => _types;
    public IAvaloniaReadOnlyList<InboxMutedSenderViewModel> MutedSenders => _muted;
    public bool PassesOffered { get; }

    public bool IsLoading {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? LoadError {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsLoaded {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string SlackNote {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>Slack is connected and disconnected in Chat accounts, on the web.</summary>
    public bool CanOpenChatAccounts {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool TellAgent {
        get;
        set {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            if (!_applyingControls) _ = SetTellAgentAsync(value);
        }
    }

    public bool HasMutedSenders => _muted.Count > 0;

    /// <summary>The pass controls loaded: until then the section shows their error and a retry, never default values.</summary>
    public bool PassesLoaded {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? PassesError {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>False while no one is signed in: a switch flipped then would write for whoever signs in next.</summary>
    public bool CanWrite {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    bool _applyingControls;

    public ReactiveCommand<Unit, Unit> RetryCommand            { get; }
    public ReactiveCommand<Unit, Unit> OpenChatAccountsCommand { get; }

    public InboxSettingsViewModel(IInboxApi api, IObservable<Unit> changed, bool passesOffered, Action openChatAccounts,
            IObservable<ServerLaneStatus>? status = null) {
        _api = api;
        PassesOffered = passesOffered;
        RetryCommand = ReactiveCommand.Create(Reload);
        OpenChatAccountsCommand = ReactiveCommand.Create(openChatAccounts);
        changed.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => Reload()).DisposeWith(_disposables);
        status?.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(OnStatus).DisposeWith(_disposables);
        Reload();
    }

    // A sign-in as someone else drops what was loaded for the previous account before anything can be written.
    void OnStatus(ServerLaneStatus status) {
        var connected = status.State == ServerLaneState.Connected;
        var signedInAsOther = connected && _subject is not null && status.Subject is not null && status.Subject != _subject;
        if (connected && status.Subject is not null) _subject = status.Subject;
        if (_connected == connected && !signedInAsOther) return;
        _connected = connected;
        CanWrite = connected;
        _generation++;
        _pendingWrites.Clear();
        if (signedInAsOther) {
            IsLoaded = false;
            PassesLoaded = false;
            _types.Clear();
            _muted.Clear();
            this.RaisePropertyChanged(nameof(HasMutedSenders));
        }
        if (connected) Reload();
    }

    public void Reload() {
        if (_disposed || _mutations > 0) return;
        _ = LoadAsync(++_generation);
    }

    async Task LoadAsync(int generation) {
        IsLoading = !IsLoaded;
        LoadError = null;
        var prefs = await Guard(_api.PrefsAsync(_lifetime.Token));
        var controls = PassesOffered ? await Guard(_api.PassControlsAsync(_lifetime.Token)) : null;
        if (_disposed || generation != _generation) return;
        IsLoading = false;
        if (prefs is not { Kind: InboxReadKind.Ready, Value: { } value }) {
            if (!IsLoaded) LoadError = "Couldn't load your notification preferences.";
            return;
        }
        ApplyPrefs(value);
        IsLoaded = true;
        if (!PassesOffered) return;
        if (controls is { Kind: InboxReadKind.Ready, Value: { } c }) {
            ApplyControls(c);
            PassesLoaded = true;
            PassesError = null;
        } else if (!PassesLoaded) {
            PassesError = "Couldn't load your pass settings.";
        }
    }

    // A write invalidates every read already in flight; once the last write settles, one fresh read shows the result.
    async Task MutateAsync(Func<Task> write) {
        _generation++;
        _mutations++;
        try {
            await write();
        } finally {
            _mutations--;
        }
        if (_mutations == 0) Reload();
    }

    void ApplyPrefs(NotificationPrefsDto prefs) {
        var connected = prefs.Slack.Delivery == "connected";
        SlackNote = !prefs.Slack.TenantConfigured ? "Ask your admin to set up the Slack notifications app to receive these in Slack."
            : connected                            ? "Enabled types arrive as Slack direct messages"
            : prefs.Slack.Delivery == "needs_reconnect" ? "Slack delivery stopped. Reconnect Slack in Chat accounts on the kcap web app to resume"
            : "Connect Slack in Chat accounts on the kcap web app to receive these in Slack";
        CanOpenChatAccounts = prefs.Slack.TenantConfigured && !connected;

        var same = _types.Count == prefs.Types.Count && _types.Zip(prefs.Types).All(p => p.First.Type == p.Second.Type && p.First.SlackUsable == (p.Second.SlackCapable && connected));
        if (same) {
            foreach (var (row, dto) in _types.Zip(prefs.Types)) row.Apply(dto);
            return;
        }
        _types.Clear();
        _types.AddRange(prefs.Types.Select(t => new InboxTypeRowViewModel(t, connected, WriteTypeAsync)));
    }

    void ApplyControls(PassControlsDto controls) {
        _applyingControls = true;
        try {
            TellAgent = !controls.AgentOptedOut;
        } finally {
            _applyingControls = false;
        }
        _muted.Clear();
        _muted.AddRange(controls.MutedSenders.Select(s => new InboxMutedSenderViewModel(s.UserId, s.Name,
            ReactiveCommand.CreateFromTask(() => UnmuteAsync(s.UserId)))));
        this.RaisePropertyChanged(nameof(HasMutedSenders));
    }

    async Task WriteTypeAsync(InboxTypeRowViewModel row, bool enabled, bool slackEnabled) {
        if (_disposed) return;
        if (!_connected) {
            Reload();
            return;
        }
        _pendingWrites[row.Type] = (enabled, slackEnabled);
        if (!_writing.Add(row.Type)) return;
        try {
            while (!_disposed && _pendingWrites.Remove(row.Type, out var pair))
                await MutateAsync(() => Guard(_api.PutPrefAsync(row.Type, pair.Enabled, pair.SlackEnabled, _lifetime.Token)));
        } finally {
            _writing.Remove(row.Type);
        }
    }

    Task SetTellAgentAsync(bool tell) => ControlsAsync(() => _api.SetAgentOptOutAsync(!tell, _lifetime.Token));

    Task UnmuteAsync(string senderId) => ControlsAsync(() => _api.UnmuteAsync(senderId, _lifetime.Token));

    Task ControlsAsync(Func<Task<InboxRead<PassControlsDto>>> write) {
        if (_disposed) return Task.CompletedTask;
        if (!_connected) {
            Reload();
            return Task.CompletedTask;
        }
        return MutateAsync(async () => {
            var read = await Guard(write());
            if (!_disposed && read is { Kind: InboxReadKind.Ready, Value: { } c }) ApplyControls(c);
        });
    }

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
