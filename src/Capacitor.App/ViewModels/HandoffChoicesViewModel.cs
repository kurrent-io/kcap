using System.Collections.Frozen;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The hand-off strip's chips. A hand-off always launches on this machine's daemon, so the agents,
/// models and accounts are the local daemon's. An account picked here is remembered beside the
/// launcher's, under the checkout the hand-off starts in.
public sealed class HandoffChoicesViewModel : ReactiveObject, ILaunchChoices, IDisposable {
    readonly CompositeDisposable _disposables = new();
    readonly ObservableAsPropertyHelper<IReadOnlyList<HarnessOption>> _harnesses;
    readonly ObservableAsPropertyHelper<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>> _catalog;
    readonly AccountChooser _accounts;
    readonly AppStateStore? _state;
    readonly BehaviorSubject<(string Vendor, string AccountId)?> _accountCatalogKey = new(null);
    IReadOnlyDictionary<string, string>? _accountMemory;
    // Picks made before the stored memory loaded, replayed over it so the older read cannot win.
    List<(string HostId, string Vendor, string RepoPath, bool Remote, string? AccountId)>? _earlyPicks = [];
    (string Vendor, HandoffAccountChoice Choice)? _restored;
    HandoffAccountChoice? _picked;
    Task _accountMemoryWrites = Task.CompletedTask;
    AccountSupport _support;
    AccountsSnapshotDto? _snapshot;

    /// sessionVendor is the ended session's own agent, kept while this daemon hosts it; repoPath is
    /// where it ran, until the composer names the checkout the hand-off starts in.
    /// <param name="readings">The local daemon's accounts snapshot and the usage clock; null offers no account.</param>
    /// <param name="state">Where the launcher remembers an account per repository; null remembers nothing.</param>
    public HandoffChoicesViewModel(
            IDaemonClientService daemon, string? sessionVendor,
            IObservable<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>>? serverCatalog = null,
            string? repoPath = null, AccountReadings? readings = null, AppStateStore? state = null) {
        _selectedVendor = string.IsNullOrWhiteSpace(sessionVendor) ? HomeViewModel.DefaultVendor : sessionVendor;
        RepoPath = repoPath ?? "";
        _state = state;
        _accounts = new AccountChooser(
            (hostId, vendor, path, remote) => AccountMemory.Lookup(_accountMemory, hostId, vendor, path, remote), RememberAccount);
        _accounts.Changed += PublishAccountChoice;
        _accounts.EnterTarget(RepoPath, _selectedVendor, remote: false);

        // ObserveOn before ToProperty: snapshots arrive on the daemon client's own thread.
        var options = daemon.Snapshots.Select(s => HostedHarnessCatalog.Build(s.Daemon.SupportedVendors));
        _harnesses = options
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.Harnesses, HostedHarnessCatalog.Build(null))
            .DisposeWith(_disposables);
        options
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(Revalidate)
            .DisposeWith(_disposables);

        _catalog = daemon.Snapshots
            .Select(s => (Models: VendorModelMaps.FromStatus(s.Daemon.VendorModels), ByAccount: VendorModelMaps.FromStatus(s.Daemon.VendorModelsByAccount)))
            .StartWith((Models: null, ByAccount: null))
            .CombineLatest(serverCatalog ?? Observable.Return(ServerVendorModelCatalog.Empty), _accountCatalogKey.DistinctUntilChanged(),
                (machine, server, account) => HomeViewModel.WithAccountCatalog(
                    HomeViewModel.MergeCatalogs(machine.Models, server), machine.ByAccount, account))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.EffectiveModelCatalog, ServerVendorModelCatalog.Empty)
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.EffectiveModelCatalog)
            .Scan((Prev: ServerVendorModelCatalog.Empty, Next: ServerVendorModelCatalog.Empty), (acc, next) => (acc.Next, next))
            .Subscribe(pair => {
                if (HomeViewModel.ShouldClearSelection(SelectedVendor, SelectedModel, pair.Prev, pair.Next)) SelectedModel = "";
            })
            .DisposeWith(_disposables);

        if (readings is null) {
            _accounts.SetSupport(AccountSupport.Unsupported, fetchFailed: false);
            MemoryLoaded(null);
        } else {
            // A daemon that can hand off can also launch on a named account, so its accounts stream
            // alone decides whether there is a choice.
            daemon.Status
                .Select(SupportOf)
                .DistinctUntilChanged()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(support => {
                    _support = support;
                    _accounts.SetSupport(support, fetchFailed: false);
                })
                .DisposeWith(_disposables);
            readings.Local
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(snapshot => {
                    _snapshot = snapshot;
                    _accounts.SetSnapshot(snapshot);
                })
                .DisposeWith(_disposables);
            readings.Local
                .CombineLatest(this.WhenAnyValue(x => x.SelectedVendor), readings.Clock, HomeViewModel.ProjectUsage)
                .DistinctUntilChanged(EqualityComparer<IReadOnlyDictionary<string, AccountUsageView>>.Create(
                    (a, b) => HomeViewModel.UsageEquals(a!, b!), d => d.Count))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(usage => AccountUsageById = usage)
                .DisposeWith(_disposables);
            if (state is null) MemoryLoaded(null);
            else
                Observable.FromAsync(state.LoadAsync)
                    .ObserveOn(RxSchedulers.MainThreadScheduler)
                    .Subscribe(loaded => MemoryLoaded(loaded.AccountByRepo), ex => {
                        Console.Error.WriteLine($"kcap: hand-off account memory: {ex.Message}");
                        MemoryLoaded(null);
                    })
                    .DisposeWith(_disposables);
        }

        RetryAccountsCommand = ReactiveCommand.Create(() => { });
    }

    void MemoryLoaded(IReadOnlyDictionary<string, string>? stored) {
        var memory = stored;
        foreach (var (hostId, vendor, repoPath, remote, accountId) in _earlyPicks ?? [])
            memory = AccountMemory.With(memory, hostId, vendor, repoPath, remote, accountId);
        _earlyPicks = null;
        _accountMemory = memory;
        _accounts.SetMemoryLoaded(true);
    }

    static AccountSupport SupportOf(AttachStatus status) => status.State switch {
        AttachState.Connected => status.Capabilities?.Contains(AccountsService.Capability) == true
            ? AccountSupport.Supported
            : AccountSupport.Unsupported,
        _ => AccountSupport.Unknown,
    };

    public IReadOnlyList<HarnessOption> Harnesses => _harnesses.Value;

    public string RepoPath {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    string _selectedVendor;
    public string SelectedVendor {
        get => _selectedVendor;
        private set => this.RaiseAndSetIfChanged(ref _selectedVendor, value);
    }

    string _selectedModel = "";
    public string SelectedModel {
        get => _selectedModel;
        set => this.RaiseAndSetIfChanged(ref _selectedModel, value);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> EffectiveModelCatalog => _catalog.Value;

    public string? UnavailableHarnessTip(HarnessOption harness) =>
        harness.Available ? null : HomeViewModel.HarnessUnavailableReason(harness.Label, null);

    public IReadOnlyList<ModelChoice> ModelChoicesFor(string vendor) =>
        EffectiveModelCatalog.TryGetValue(vendor, out var models) ? models : HostedHarnessCatalog.ModelChoicesFor(vendor);

    public Task ChooseHarnessAsync(string vendor) {
        _restored = null;
        _picked = null;
        SetVendor(vendor);
        return Task.CompletedTask;
    }

    string? _selectedEffort;
    public string? SelectedEffort {
        get => _selectedEffort;
        set => this.RaiseAndSetIfChanged(ref _selectedEffort, value);
    }

    string _selectedPermissionMode = HomeViewModel.DefaultPermissionMode;
    public string SelectedPermissionMode {
        get => _selectedPermissionMode;
        set => this.RaiseAndSetIfChanged(ref _selectedPermissionMode, value);
    }

    public Task ChoosePermissionModeAsync(string mode) {
        SelectedPermissionMode = mode;
        return Task.CompletedTask;
    }

    string _selectedCodexSandbox = HostedHarnessCatalog.DefaultCodexSandbox;
    public string SelectedCodexSandbox {
        get => _selectedCodexSandbox;
        set => this.RaiseAndSetIfChanged(ref _selectedCodexSandbox, value);
    }

    string _selectedCodexApproval = HostedHarnessCatalog.DefaultCodexApproval;
    public string SelectedCodexApproval {
        get => _selectedCodexApproval;
        set => this.RaiseAndSetIfChanged(ref _selectedCodexApproval, value);
    }

    bool _accountSection;
    public bool AccountSection {
        get => _accountSection;
        private set => this.RaiseAndSetIfChanged(ref _accountSection, value);
    }

    IReadOnlyList<AccountChoice> _accountRows = [];
    public IReadOnlyList<AccountChoice> AccountRows {
        get => _accountRows;
        private set => this.RaiseAndSetIfChanged(ref _accountRows, value);
    }

    string? _selectedAccountLabel;
    public string? SelectedAccountLabel {
        get => _selectedAccountLabel;
        private set => this.RaiseAndSetIfChanged(ref _selectedAccountLabel, value);
    }

    string? _accountPendingText;
    public string? AccountPendingText {
        get => _accountPendingText;
        private set => this.RaiseAndSetIfChanged(ref _accountPendingText, value);
    }

    public bool AccountRetryVisible => false;

    IReadOnlyDictionary<string, AccountUsageView> _accountUsageById = FrozenDictionary<string, AccountUsageView>.Empty;
    public IReadOnlyDictionary<string, AccountUsageView> AccountUsageById {
        get => _accountUsageById;
        private set => this.RaiseAndSetIfChanged(ref _accountUsageById, value);
    }

    bool _accountPending;
    /// The choice is still resolving, so a launch now would run under a default nobody chose.
    public bool AccountPending {
        get => _accountPending;
        private set => this.RaiseAndSetIfChanged(ref _accountPending, value);
    }

    /// Why the picked account cannot launch, while it is listed no longer; null otherwise.
    public string? AccountRefusal => _accounts.State == AccountSelectionState.ExplicitInvalid
        ? $"{_accounts.InvalidReason}. Use the default account or pick another."
        : null;

    /// The Default-badged row follows the daemon's default rather than pinning its account. The task
    /// completes once the choice is persisted.
    public Task PickAccountAsync(string? accountId) {
        var defaultRow = _accounts.Rows.Any(c => c.Row.IsDefault && c.Row.Id == accountId);
        _restored = null;
        _picked = new(defaultRow ? null : accountId);
        _accounts.Pick(_picked.Id);
        return _accountMemoryWrites;
    }

    public ReactiveCommand<Unit, Unit> RetryAccountsCommand { get; }

    /// The checkout the hand-off would start in, which the remembered account is read under. A pick
    /// made before it was found is the person's choice for it too.
    public void SetRepository(string? repoPath) {
        if (repoPath is not { Length: > 0 } path || PlatformPaths.Comparer.Equals(path, RepoPath)) return;
        RepoPath = path;
        _accounts.EnterTarget(path, SelectedVendor, remote: false);
        if (_picked is { } picked) _accounts.Pick(picked.Id);
        else ReapplyRestored();
    }

    /// What a hand-off started now would launch on; null while it is still resolving.
    public HandoffAccountChoice? CurrentAccount => AccountChoice();

    /// A returned draft's account outlasts the target changes that would otherwise drop it, until
    /// the person picks an account or an agent themselves.
    void ReapplyRestored() {
        if (_restored is { } restored && string.Equals(restored.Vendor, SelectedVendor, StringComparison.OrdinalIgnoreCase))
            _accounts.Restore(restored.Choice.Id);
    }

    /// The chips a draft was sent with. The vendor goes through the same check a snapshot does.
    public void Restore(HandoffDraft draft) {
        SetVendor(draft.Vendor);
        Revalidate(Harnesses);
        if (string.Equals(SelectedVendor, draft.Vendor, StringComparison.OrdinalIgnoreCase)) {
            SelectedModel = draft.Model;
            if (draft.Account is { } account) {
                _restored = (draft.Vendor, account);
                ReapplyRestored();
            }
        }
        SelectedEffort = draft.Effort;
        if (draft.PermissionMode is { } mode) SelectedPermissionMode = mode;
        if (draft.CodexPosture is { } posture) {
            SelectedCodexSandbox = posture.Sandbox;
            SelectedCodexApproval = posture.Approval;
        }
    }

    public HandoffDraft Capture(string text, IReadOnlyList<StagedAttachment> attachments) =>
        new(text, attachments, SelectedVendor, SelectedModel, SelectedEffort, SelectedPermissionMode,
            new CodexLaunchPosture(SelectedCodexSandbox, SelectedCodexApproval),
            AccountChoice(), null);

    /// An unavailable pick keeps its id, so the launch refuses it rather than falling back to the default.
    HandoffAccountChoice? AccountChoice() => _accounts.State switch {
        AccountSelectionState.Pending => _restored is { } restored
            && string.Equals(restored.Vendor, SelectedVendor, StringComparison.OrdinalIgnoreCase) ? restored.Choice : null,
        AccountSelectionState.ExplicitValid or AccountSelectionState.ExplicitInvalid => new(_accounts.SelectedAccountId),
        _ => new(null),
    };

    /// Read against the latest snapshot rather than the chooser: an upload is long enough for the
    /// account to have gone from under the draft.
    public string? AccountErrorFor(HandoffDraft draft) {
        if (draft.Account?.Id is not { } id) return null;
        var listed = _snapshot is not null && Services.AccountRows.For(_snapshot, draft.Vendor).Any(r => r.Id == id);
        if (listed && _support == AccountSupport.Supported) return null;
        var label = _snapshot?.Accounts?.FirstOrDefault(a => a.Id == id)?.Label ?? SelectedAccountLabel ?? id;
        return $"{AccountChooser.UnavailableReason(label)}. Use the default account or pick another.";
    }

    /// Model ids are vendor-specific, so a vendor change drops the model.
    void SetVendor(string vendor) {
        var changed = !string.Equals(vendor, SelectedVendor, StringComparison.OrdinalIgnoreCase);
        if (changed) SelectedModel = "";
        SelectedVendor = vendor;
        if (!changed) return;
        _picked = null;
        _accounts.EnterTarget(RepoPath, vendor, remote: false);
        ReapplyRestored();
    }

    void Revalidate(IReadOnlyList<HarnessOption> options) {
        var current = options.FirstOrDefault(o => string.Equals(o.Vendor, SelectedVendor, StringComparison.OrdinalIgnoreCase));
        if (current is { Available: true }) return;
        SetVendor(options.Any(o => o.Available && o.Vendor == HomeViewModel.DefaultVendor)
            ? HomeViewModel.DefaultVendor
            : options.FirstOrDefault(o => o.Available)?.Vendor ?? HomeViewModel.DefaultVendor);
    }

    /// AccountPending goes last, so whatever resumes when it clears sees the whole choice published.
    void PublishAccountChoice() {
        AccountSection = _accounts.ShowsSection;
        AccountRows = _accounts.Rows;
        AccountPendingText = _accounts.PendingText;
        SelectedAccountLabel = _accounts.SelectedLabel;
        this.RaisePropertyChanged(nameof(AccountRefusal));
        _accountCatalogKey.OnNext(_accounts.State is AccountSelectionState.ExplicitValid or AccountSelectionState.ExplicitInvalid
            ? (SelectedVendor, _accounts.SelectedAccountId!) : null);
        AccountPending = _accounts.State == AccountSelectionState.Pending;
    }

    /// Written in call order: the chooser also forgets an entry from inside its recompute.
    void RememberAccount(string hostId, string vendor, string repoPath, bool remote, string? accountId) {
        _accountMemory = AccountMemory.With(_accountMemory, hostId, vendor, repoPath, remote, accountId);
        _earlyPicks?.Add((hostId, vendor, repoPath, remote, accountId));
        if (_state is not { } state) return;
        _accountMemoryWrites = WriteAfterAsync(_accountMemoryWrites);

        async Task WriteAfterAsync(Task previous) {
            await previous.ConfigureAwait(false);
            await state.UpdateAsync(s => s with {
                AccountByRepo = AccountMemory.With(s.AccountByRepo, hostId, vendor, repoPath, remote, accountId),
            }).ConfigureAwait(false);
        }
    }

    public void Dispose() {
        _disposables.Dispose();
        _accountCatalogKey.Dispose();
    }
}
