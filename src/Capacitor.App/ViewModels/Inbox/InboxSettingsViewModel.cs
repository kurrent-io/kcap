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
    int _generation;
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

    bool _applyingControls;

    public ReactiveCommand<Unit, Unit> RetryCommand            { get; }
    public ReactiveCommand<Unit, Unit> OpenChatAccountsCommand { get; }

    public InboxSettingsViewModel(IInboxApi api, IObservable<Unit> changed, bool passesOffered, Action openChatAccounts) {
        _api = api;
        PassesOffered = passesOffered;
        RetryCommand = ReactiveCommand.Create(Reload);
        OpenChatAccountsCommand = ReactiveCommand.Create(openChatAccounts);
        changed.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => Reload()).DisposeWith(_disposables);
        Reload();
    }

    public void Reload() {
        if (_disposed) return;
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
        if (controls is { Kind: InboxReadKind.Ready, Value: { } c }) ApplyControls(c);
        IsLoaded = true;
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
        await Guard(_api.PutPrefAsync(row.Type, enabled, slackEnabled, _lifetime.Token));
        Reload();
    }

    async Task SetTellAgentAsync(bool tell) {
        var read = await Guard(_api.SetAgentOptOutAsync(!tell, _lifetime.Token));
        if (!_disposed && read is { Kind: InboxReadKind.Ready, Value: { } c }) ApplyControls(c);
        else Reload();
    }

    async Task UnmuteAsync(string senderId) {
        var read = await Guard(_api.UnmuteAsync(senderId, _lifetime.Token));
        if (!_disposed && read is { Kind: InboxReadKind.Ready, Value: { } c }) ApplyControls(c);
        else Reload();
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
