using System.ComponentModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core.Setup.Intelligence;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// <summary>
/// The local server's background intelligence in Settings: who runs it, whether imported sessions get outcomes, and
/// the daily limit on older sessions. Everything goes through the bundled CLI, which keeps the installation's
/// intelligence record in step with the server, as onboarding does.
/// </summary>
/// <remarks>
/// One operation runs at a time, and none while an upgrade or a search change does; each of those is held off while
/// one of these runs, through <see cref="Busy"/>. A read that lands after a later operation started is dropped. A
/// group with an unsaved draft keeps it through a read, and says when the server's value under it changed.
/// </remarks>
public sealed class BackgroundIntelligenceSettingsViewModel : ReactiveObject, IDisposable {
    public const string Subtitle =
        "Choose what runs the model calls behind titles, outcomes, turn summaries and evals, and how fast it works through older sessions";

    public const string PinnedNote       = "The server's service environment sets this";
    public const string CustomNote       = "The server's web settings configure this. Choosing an option and saving replaces it";
    public const string ReviewAgain      = "Changed elsewhere. Review and save again";
    public const string ReadBackFailed   = "The change was saved, but the settings could not be read back";
    public const string LimitHelp        =
        "Keeps a large import from using up your subscription or API key in an afternoon. Sessions from today are never held back";
    public static readonly string LimitRangeHint = $"Enter a whole number from {IntelligenceSettings.DailyLimitRange}";

    static readonly string[] Derived = [
        nameof(Busy), nameof(Held), nameof(HeldText), nameof(IsLoading), nameof(ShowsControls), nameof(NeedsUpdate), nameof(NeedsUpdateText),
        nameof(ReadError), nameof(CanRetry), nameof(CanEditMode), nameof(CanEditPreferences), nameof(ProviderSelected), nameof(CustomText),
        nameof(WaitingText), nameof(ModeError), nameof(ModeNotice), nameof(ModeChangedText), nameof(CanSaveMode), nameof(KeyHint),
        nameof(ImportOutcomes), nameof(CanToggleOutcomes), nameof(OutcomesPinnedNote), nameof(OutcomesError),
        nameof(DailyLimitText), nameof(LimitReadOnly), nameof(CanEditLimit), nameof(LimitHint), nameof(LimitPinnedNote),
        nameof(LimitChangedText), nameof(LimitError), nameof(CanSaveLimit), nameof(Model), nameof(ApiKey), nameof(SelectedVendor),
    ];

    readonly IKcapCli                                _cli;
    readonly Func<CancellationToken, Task<string[]>> _headless;
    readonly LocalUpgradeController                  _upgrade;
    readonly LocalSearchController                   _search;
    readonly CancellationTokenSource                 _lifetime    = new();
    readonly CompositeDisposable                     _disposables = new();

    IntelligenceSettings? _server;
    // False after a saved mode could not be read back: the preferences shown may be stale.
    bool                  _fresh;
    bool                  _needsUpdate;
    bool                  _disposed;
    bool                  _visible;
    bool                  _writing;
    bool                  _settingsBusy;
    bool                  _applying;
    int                   _running;
    int                   _sequence;
    bool?                 _pendingOutcomes;
    IntelligenceVendor    _vendor = IntelligenceVendor.Anthropic;
    string                _model  = IntelligenceVendor.Anthropic.DefaultModel;
    string                _apiKey = "";
    string                _limitText = "";
    string?               _readError, _modeError, _modeNotice, _modeChanged, _outcomesError, _limitChanged, _limitError;

    /// <param name="headless">The agents this computer can run headless, by the source onboarding uses.</param>
    public BackgroundIntelligenceSettingsViewModel(
            IKcapCli cli, Func<CancellationToken, Task<string[]>> headless, LocalUpgradeController upgrade, LocalSearchController search) {
        _cli      = cli;
        _headless = headless;
        _upgrade  = upgrade;
        _search   = search;

        Options = [.. IntelligenceAnswer.Harnesses.Select(BackgroundIntelligenceOption.LocalAgent),
            BackgroundIntelligenceOption.Provider(), BackgroundIntelligenceOption.None()];
        foreach (var option in Options) option.PropertyChanged += OnOptionChanged;
        Disposable.Create(() => { foreach (var option in Options) option.PropertyChanged -= OnOptionChanged; }).DisposeWith(_disposables);

        // An upgrade or a search change restarts the server, so the settings are read again once it ends.
        upgrade.WhenAnyValue(x => x.Running).Merge(search.WhenAnyValue(x => x.Running))
            .Skip(2)
            .Subscribe(running => {
                Refresh();
                if (!running && !Held && _visible) _ = RefreshAsync();
            })
            .DisposeWith(_disposables);

        SaveModeCommand    = ReactiveCommand.CreateFromTask(SaveModeAsync);
        SaveLimitCommand   = ReactiveCommand.CreateFromTask(SaveLimitAsync);
        RetryCommand       = ReactiveCommand.CreateFromTask(RefreshAsync);
        RevertModeCommand  = ReactiveCommand.Create(RevertMode);
        RevertLimitCommand = ReactiveCommand.Create(RevertLimit);
    }

    public IReadOnlyList<BackgroundIntelligenceOption> Options { get; }

    public IReadOnlyList<IntelligenceVendor> Vendors => IntelligenceVendor.All;

    public ReactiveCommand<Unit, Unit> SaveModeCommand    { get; }
    public ReactiveCommand<Unit, Unit> SaveLimitCommand   { get; }
    public ReactiveCommand<Unit, Unit> RetryCommand       { get; }
    public ReactiveCommand<Unit, Unit> RevertModeCommand  { get; }
    public ReactiveCommand<Unit, Unit> RevertLimitCommand { get; }

    /// <summary>An operation of this tab is running; an upgrade or a search change waits for it.</summary>
    public bool Busy => !_disposed && _running > 0;

    public const string DaemonBusyText = "Settings is changing the daemon. Try again once it has finished";

    public bool Held => _upgrade.Running || _search.Running || _settingsBusy;

    public string? HeldText => _upgrade.Running || _search.Running ? LocalSearchViewModel.BusyText
        : _settingsBusy ? DaemonBusyText
        : null;

    /// <summary>The Settings window's own daemon operation runs. A rename can relaunch the app, which would dispose
    /// this tab before the CLI records a mode the server took, so nothing starts here until it ends.</summary>
    public bool SettingsBusy {
        get => _settingsBusy;
        set {
            if (value == _settingsBusy) return;
            _settingsBusy = value;
            Refresh();
            if (!value && !Held && _visible) _ = RefreshAsync();
        }
    }

    public bool IsLoading => _server is null && Busy && !_needsUpdate;

    public bool NeedsUpdate => _needsUpdate;

    public string? NeedsUpdateText => _needsUpdate ? Line(IntelligenceRefusals.Describe(IntelligenceSettingsClient.NeedsUpdate)) : null;

    public bool ShowsControls => _server is not null && !_needsUpdate;

    public string? ReadError => _readError;

    public bool CanRetry => _readError is not null && !Busy && !Held;

    public bool CanEditMode => !_disposed && _server is not null && !Busy && !Held;

    public bool CanEditPreferences => CanEditMode && _fresh;

    BackgroundIntelligenceOption? Selected => Options.FirstOrDefault(o => o.IsSelected);

    public bool ProviderSelected => Selected?.Token == BackgroundIntelligenceOption.ProviderToken;

    public string? CustomText => _server?.Mode == IntelligenceModes.Custom && Selected is null ? CustomNote : null;

    /// The configured agent is missing from this computer; it stays selected rather than another being chosen for it.
    public string? WaitingText =>
        Selected is { IsAgent: true, Found: false } agent && agent.Token == ServerToken(_server)
            ? $"Background work waits until {HeadlessHarnessProbe.Label(agent.Token)} is back on this computer"
            : null;

    public IntelligenceVendor SelectedVendor {
        get => _vendor;
        set {
            if (value == _vendor || _applying) return;
            _vendor = value;
            _model  = value.DefaultModel;
            _apiKey = "";
            Refresh();
        }
    }

    public string Model {
        get => _model;
        set {
            if (value == _model || _applying) return;
            _model = value;
            Refresh();
        }
    }

    public string ApiKey {
        get => _apiKey;
        set {
            if (value == _apiKey || _applying) return;
            _apiKey = value;
            Refresh();
        }
    }

    public string KeyHint => $"Your {SelectedVendor.Label} API key. The local server stores it and sends it only to {SelectedVendor.Label}";

    public string? ModeError => _modeError;

    public string? ModeNotice => _modeNotice;

    public string? ModeChangedText => ModeDirty(_server) ? _modeChanged : null;

    public bool CanSaveMode =>
        CanEditMode && Selected is { CanSelect: true } && ModeDirty(_server) && (!ProviderSelected || !string.IsNullOrWhiteSpace(ApiKey));

    public bool ImportOutcomes {
        get => _pendingOutcomes ?? _server?.ImportOutcomes ?? false;
        set {
            if (_applying || value == ImportOutcomes) return;
            _ = SetImportOutcomesAsync(value);
        }
    }

    public bool CanToggleOutcomes => CanEditPreferences && _server is { ImportOutcomesPinned: false };

    public string? OutcomesPinnedNote => _server is { ImportOutcomesPinned: true } ? PinnedNote : null;

    public string? OutcomesError => _outcomesError;

    public string DailyLimitText {
        get => _limitText;
        set {
            if (value == _limitText || _applying) return;
            _limitText    = value;
            _limitError   = null;
            Refresh();
        }
    }

    public bool LimitReadOnly => _server is { BacklogLimitPinned: true };

    public bool CanEditLimit => CanEditPreferences && !LimitReadOnly;

    int? TypedLimit => int.TryParse(_limitText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && IntelligenceSettings.IsDailyLimit(n)
        ? n : null;

    public string? LimitHint => _server is not null && TypedLimit is null ? LimitRangeHint : null;

    public string? LimitPinnedNote => LimitReadOnly ? PinnedNote : null;

    public string? LimitChangedText => LimitDirty(_server) ? _limitChanged : null;

    public string? LimitError => _limitError;

    public bool CanSaveLimit => CanEditLimit && LimitDirty(_server) && TypedLimit is not null;

    /// <summary>The tab is on screen: the settings are read again, and again on every refocus until it is hidden.</summary>
    public Task ShownAsync() {
        _visible = true;
        return RefreshAsync();
    }

    public void Hidden() => _visible = false;

    public Task RefocusedAsync() => _visible ? RefreshAsync() : Task.CompletedTask;

    /// A read supersedes one still running; it never starts under a write or while the installation is held.
    public async Task RefreshAsync() {
        if (_disposed || _writing || Held) return;

        var seq = Begin(write: false);
        try {
            var (outcome, found) = await ReadAsync(_lifetime.Token).ConfigureAwait(true);
            if (seq != _sequence || _disposed) return;

            switch (outcome) {
                case IntelligenceSettingsOutcome.Read { Settings: var settings }:
                    MarkFound(found);
                    Apply(settings);
                    _fresh       = true;
                    _readError   = null;
                    _needsUpdate = false;
                    break;
                case IntelligenceSettingsOutcome.Refused { Error: IntelligenceSettingsClient.NeedsUpdate }:
                    _needsUpdate = true;
                    break;
                case IntelligenceSettingsOutcome.Refused { Error: var code }:
                    _readError = Line(IntelligenceRefusals.Describe(code));
                    break;
                case IntelligenceSettingsOutcome.Failed { Problem: var problem }:
                    _readError = Line($"Could not read the settings: {problem}");
                    break;
            }
        } catch (OperationCanceledException) when (_disposed) {
        } finally {
            End(write: false);
        }
    }

    /// Reads first: a change of who runs it made elsewhere since the tab showed it is shown instead of overwritten.
    public async Task SaveModeAsync() {
        if (!CanSaveMode || Selected is not { } selected || _server is not { } shown) return;

        var answer = selected.IsAgent ? IntelligenceAnswer.LocalAgent(selected.Token)
            : selected.Token == BackgroundIntelligenceOption.ProviderToken ? IntelligenceAnswer.Provider(_vendor)
            : IntelligenceAnswer.None;
        var model = answer.Vendor is { } vendor && _model.Trim() is { Length: > 0 } typed && typed != vendor.DefaultModel ? typed : null;
        var key   = answer.Vendor is null ? null : _apiKey.Trim();

        Begin(write: true);
        _modeError  = null;
        _modeNotice = null;
        try {
            var ct = _lifetime.Token;
            switch (await _cli.LocalIntelligenceStatusAsync(ct).ConfigureAwait(true)) {
                case IntelligenceSettingsOutcome.Read { Settings: var latest } when !SameMode(shown, latest):
                    Apply(latest, resetMode: true);
                    _modeNotice = ReviewAgain;
                    return;
                case IntelligenceSettingsOutcome.Read { Settings: var latest }:
                    Apply(latest);
                    break;
                case var other:
                    _modeError = Problem(other);
                    return;
            }

            switch (await _cli.LocalIntelligenceAsync(answer, model, key, null, ct).ConfigureAwait(true)) {
                case IntelligenceSetupOutcome.Applied { Response: var applied }:
                    if (await _cli.LocalIntelligenceStatusAsync(ct).ConfigureAwait(true) is IntelligenceSettingsOutcome.Read { Settings: var after }) {
                        Apply(after, resetMode: true);
                        _fresh     = true;
                        _readError = null;
                    } else {
                        Apply(_server! with {
                            Mode = applied.Mode, Harness = applied.Harness, Vendor = applied.Vendor,
                            Model = applied.Vendor is null ? null : model ?? answer.Vendor?.DefaultModel,
                        }, resetMode: true);
                        _fresh     = false;
                        _readError = ReadBackFailed;
                    }
                    break;
                case IntelligenceSetupOutcome.Refused { Error: var code }:
                    _modeError = Line(IntelligenceRefusals.Describe(code));
                    break;
                case IntelligenceSetupOutcome.Failed { Problem: var problem }:
                    _modeError = Line($"Could not reach the local server: {problem}");
                    break;
            }
        } catch (OperationCanceledException) when (_disposed) {
        } finally {
            End(write: true);
        }
    }

    public Task SetImportOutcomesAsync(bool value) {
        if (!CanToggleOutcomes || value == _server!.ImportOutcomes) {
            Refresh();
            return Task.CompletedTask;
        }

        _outcomesError = null;
        return PreferAsync(new(value, null), value, error => _outcomesError = error);
    }

    public Task SaveLimitAsync() {
        if (!CanSaveLimit || TypedLimit is not { } limit) return Task.CompletedTask;

        _limitError = null;
        return PreferAsync(new(null, limit), null, error => _limitError = error);
    }

    async Task PreferAsync(IntelligencePreferencesRequest request, bool? pendingOutcomes, Action<string> refused) {
        _pendingOutcomes = pendingOutcomes;
        Begin(write: true);
        try {
            switch (await _cli.LocalIntelligencePreferencesAsync(request.ImportOutcomes, request.BacklogSessionTouchesPerDay, _lifetime.Token)
                        .ConfigureAwait(true)) {
                case IntelligenceSettingsOutcome.Read { Settings: var settings }:
                    Apply(settings, resetLimit: request.BacklogSessionTouchesPerDay is not null);
                    _needsUpdate = false;
                    break;
                case IntelligenceSettingsOutcome.Refused { Error: IntelligenceSettingsClient.NeedsUpdate }:
                    _needsUpdate = true;
                    break;
                case var other:
                    refused(Problem(other));
                    break;
            }
        } catch (OperationCanceledException) when (_disposed) {
        } finally {
            _pendingOutcomes = null;
            End(write: true);
        }
    }

    async Task<(IntelligenceSettingsOutcome Outcome, string[] Found)> ReadAsync(CancellationToken ct) {
        var found   = FoundAsync(ct);
        var outcome = await _cli.LocalIntelligenceStatusAsync(ct).ConfigureAwait(true);

        return (outcome, await found.ConfigureAwait(true));
    }

    async Task<string[]> FoundAsync(CancellationToken ct) {
        try {
            return await _headless(ct).ConfigureAwait(true);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap app: headless harness read failed: {ex.Message}");
            return [];
        }
    }

    void MarkFound(string[] found) {
        foreach (var option in Options.Where(o => o.IsAgent)) option.Found = found.Contains(option.Token, StringComparer.OrdinalIgnoreCase);
    }

    /// Shows what the server reported. A group with a draft keeps it, noting a change under it; a reset group takes
    /// the server's value whatever was drafted.
    void Apply(IntelligenceSettings settings, bool resetMode = false, bool resetLimit = false) {
        var previous = _server;

        if (resetMode || !ModeDirty(previous)) {
            SelectFrom(settings);
            _modeChanged = null;
        } else if (previous is not null && !SameMode(previous, settings)) {
            _modeChanged = $"Changed elsewhere to {Describe(settings)}";
        }

        var serverLimit = settings.BacklogSessionTouchesPerDay.ToString(CultureInfo.InvariantCulture);
        if (resetLimit || !LimitDirty(previous)) {
            _limitText    = serverLimit;
            _limitChanged = null;
        } else if (previous!.BacklogSessionTouchesPerDay != settings.BacklogSessionTouchesPerDay) {
            _limitChanged = $"Changed elsewhere to {serverLimit}";
        }

        _server = settings;
    }

    void SelectFrom(IntelligenceSettings settings) {
        var token = ServerToken(settings);
        _applying = true;
        try {
            foreach (var option in Options) option.IsSelected = option.Token == token;
        } finally {
            _applying = false;
        }

        _vendor = IntelligenceVendor.All.FirstOrDefault(v => v.Id == settings.Vendor) ?? IntelligenceVendor.Anthropic;
        _model  = settings.Model ?? _vendor.DefaultModel;
        _apiKey = "";
    }

    void RevertMode() {
        if (_server is null) return;
        SelectFrom(_server);
        _modeChanged = null;
        Refresh();
    }

    void RevertLimit() {
        if (_server is null) return;
        _limitText    = _server.BacklogSessionTouchesPerDay.ToString(CultureInfo.InvariantCulture);
        _limitChanged = null;
        Refresh();
    }

    void OnOptionChanged(object? sender, PropertyChangedEventArgs e) {
        if (_applying || e.PropertyName != nameof(BackgroundIntelligenceOption.IsSelected)
         || sender is not BackgroundIntelligenceOption { IsSelected: true } picked) return;

        _applying = true;
        try {
            foreach (var other in Options.Where(o => o != picked)) other.IsSelected = false;
        } finally {
            _applying = false;
        }

        _modeError  = null;
        _modeNotice = null;
        Refresh();
    }

    bool ModeDirty(IntelligenceSettings? server) {
        if (server is null || Selected is not { } selected) return false;
        if (selected.Token != ServerToken(server)) return true;

        return selected.Token == BackgroundIntelligenceOption.ProviderToken
            && (_vendor.Id != server.Vendor || _model.Trim() != (server.Model ?? _vendor.DefaultModel) || _apiKey.Trim().Length > 0);
    }

    bool LimitDirty(IntelligenceSettings? server) =>
        server is not null && _limitText.Trim() != server.BacklogSessionTouchesPerDay.ToString(CultureInfo.InvariantCulture);

    static string? ServerToken(IntelligenceSettings? settings) => settings?.Mode switch {
        IntelligenceModes.LocalAgent => settings.Harness,
        IntelligenceModes.Provider   => BackgroundIntelligenceOption.ProviderToken,
        IntelligenceModes.None       => IntelligenceAnswer.NoneToken,
        _                            => null,
    };

    static bool SameMode(IntelligenceSettings a, IntelligenceSettings b) =>
        a.Mode == b.Mode && a.Harness == b.Harness && a.Vendor == b.Vendor && (a.Mode != IntelligenceModes.Provider || a.Model == b.Model);

    static string Describe(IntelligenceSettings settings) => settings.Mode switch {
        IntelligenceModes.LocalAgent => Onboarding.IntelligenceOption.AgentLabel(settings.Harness ?? ""),
        IntelligenceModes.Provider   => $"{IntelligenceVendor.All.FirstOrDefault(v => v.Id == settings.Vendor)?.Label ?? settings.Vendor} ({settings.Model})",
        IntelligenceModes.None       => "None",
        _                            => "an arrangement the server's web settings made",
    };

    static string Problem(IntelligenceSettingsOutcome outcome) => outcome switch {
        IntelligenceSettingsOutcome.Refused { Error: var code }    => Line(IntelligenceRefusals.Describe(code)),
        IntelligenceSettingsOutcome.Failed { Problem: var problem } => Line($"Could not reach the local server: {problem}"),
        _                                                          => "The local server did not answer",
    };

    /// The CLI's sentences end in a full stop, which a line on this tab never does.
    static string Line(string sentence) => sentence.TrimEnd().TrimEnd('.');

    int Begin(bool write) {
        _running++;
        _writing |= write;
        var seq = ++_sequence;
        Refresh();
        return seq;
    }

    void End(bool write) {
        _running--;
        if (write) _writing = false;
        Refresh();
    }

    void Refresh() {
        foreach (var name in Derived) this.RaisePropertyChanged(name);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _disposables.Dispose();
    }
}
