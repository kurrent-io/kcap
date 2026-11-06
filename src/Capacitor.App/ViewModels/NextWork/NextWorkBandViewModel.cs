using System.Reactive;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.NextWork;

/// <summary>The launcher's quick start: what the web dashboard would have the viewer do next (who waits on them, then
/// the top picks from their own work), read from the same server snapshot and grouped by the same rules. It lists only
/// what the viewer can start or act on from here; the full overview stays on the dashboard. Pressing a row fills the
/// composer, so the launch is the composer's own; ▶ starts an agent through the server's next-work launch, which
/// claims the loose end and attaches the work item as the web does. Shown while its experimental switch is on and
/// the server has next work. While the pointer or focus is inside, a refreshed snapshot waits, so a row never moves
/// under the viewer.</summary>
public sealed class NextWorkBandViewModel : ReactiveObject, IDisposable {
    /// <summary>Rows the list holds: everyone waiting on the viewer, then the picks, up to this many.</summary>
    public const int MaxRows = 6;

    readonly INextWorkApi _api;
    readonly TimeProvider _time;
    readonly NextWorkSeenStore? _seen;
    readonly Action<string>? _openUrl;
    readonly Action<string>? _openSession;
    readonly Action<string>? _notify;
    readonly Action<string>? _launched;
    readonly string? _localDaemon;
    readonly bool _canLaunch;
    readonly Func<bool> _enabled;
    readonly Func<NextWorkFill, Task>? _fill;
    readonly Func<string, Task<string?>>? _localCheckout;
    readonly Func<string?>? _preferredVendor;

    NextWorkSnapshot? _snapshot;
    NextWorkSnapshot? _held;
    IReadOnlySet<string>? _previousKeys;
    bool _seenLoaded;
    bool _seenSaved;
    string? _filledKey;
    int _pins;
    int _refreshing;
    IDisposable? _refreshes;

    bool _unavailable;
    bool _loaded;
    bool _failed;
    bool _mutating;
    IReadOnlyList<NextWorkCardViewModel> _cards = [];
    int _more;

    public NextWorkBandViewModel(INextWorkApi api, TimeProvider time, NextWorkSeenStore? seen = null,
            Action<string>? openUrl = null, Action<string>? openSession = null, Action<string>? notify = null,
            Action<string>? launched = null, string? localDaemon = null, bool canLaunch = true, Func<bool>? enabled = null,
            Func<NextWorkFill, Task>? fill = null, Func<string, Task<string?>>? localCheckout = null,
            Func<string?>? preferredVendor = null) {
        _api             = api;
        _time            = time;
        _seen            = seen;
        _openUrl         = openUrl;
        _openSession     = openSession;
        _notify          = notify;
        _launched        = launched;
        _localDaemon     = localDaemon;
        _canLaunch       = canLaunch;
        _enabled         = enabled ?? (() => true);
        _fill            = fill;
        _localCheckout   = localCheckout;
        _preferredVendor = preferredVendor;

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
    }

    /// <summary>The list shows from the moment the switch is on, loading until its first read, so the launcher keeps
    /// its shape; only a server without next work, or no sign-in, hides it.</summary>
    public bool IsAvailable => _enabled() && !_unavailable;

    /// <summary>True until the first snapshot arrives.</summary>
    public bool IsLoading => IsAvailable && !_loaded && !_failed;

    public bool Mutating {
        get => _mutating;
        private set => this.RaiseAndSetIfChanged(ref _mutating, value);
    }

    /// <summary>Why the list may be short, for the header's status icon.</summary>
    public string? Note =>
        _failed                           ? "Suggestions couldn't be loaded. They'll retry on the next update."
      : _snapshot is { Partial: true }    ? "Suggestions are still catching up, so this list may be missing items."
      : _snapshot is { Incomplete: true } ? "Some suggestions couldn't be worked out, so this list may be missing items."
      : null;

    public bool HasNote     => Note is not null;
    public bool NoteIsError => _failed;

    public IReadOnlyList<NextWorkCardViewModel> Cards { get => _cards; private set => this.RaiseAndSetIfChanged(ref _cards, value); }

    public bool ShowEmpty => _loaded && _cards.Count == 0 && !_failed;

    /// <summary>"3 more on the dashboard" when the viewer's own work runs past the list; null when it does not. The
    /// window's Open dashboard leads there.</summary>
    public string? MoreText => _more > 0 ? $"{_more} more on the dashboard" : null;
    public bool HasMore => _more > 0;

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Refreshes on every signal; the caller delivers them on the UI thread.</summary>
    public void WatchRefreshes(IObservable<Unit> signals) {
        _refreshes?.Dispose();
        _refreshes = signals.Subscribe(_ => RefreshAsync().ContinueWith(static t => Console.Error.WriteLine($"kcap: next-work refresh failed: {t.Exception?.GetBaseException().Message}"), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default));
    }

    public void Dispose() => _refreshes?.Dispose();

    // ---- loading ----------------------------------------------------------------------------------------------

    /// <summary>Reads the snapshot. Overlapping calls collapse into the one already running.</summary>
    public async Task RefreshAsync() {
        if (!_enabled()) {
            RaiseStatus();
            return;
        }
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try {
            var read = await _api.SnapshotAsync(CancellationToken.None);
            switch (read.Kind) {
                case NextWorkReadKind.Ready when read.Value is { } dto:
                    _unavailable = false;
                    _failed = false;
                    await Accept(NextWorkSnapshot.From(dto));
                    break;
                case NextWorkReadKind.Unavailable or NextWorkReadKind.SignedOut:
                    _unavailable = true;
                    break;
                default:
                    // A failed read keeps what is on screen and says it could not refresh.
                    _failed = !_loaded;
                    break;
            }
            RaiseStatus();
        } finally {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    async Task Accept(NextWorkSnapshot snapshot) {
        if (!_seenLoaded && _seen is not null) {
            _seenLoaded = true;
            try { _previousKeys = await _seen.LoadAsync(); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: next-work seen keys unreadable: {ex.Message}"); }
        }

        if (_pins > 0 && _loaded) {
            _held = snapshot;
            return;
        }
        Apply(snapshot);
    }

    void Apply(NextWorkSnapshot snapshot) {
        _snapshot = snapshot;
        _loaded   = true;
        Arrange();
        RaiseStatus();
        SaveSeenOnce();
    }

    /// <summary>The keys shown on this visit become the next visit's "previous", once per run, as a page visit does.</summary>
    void SaveSeenOnce() {
        if (_seenSaved || _seen is null || _snapshot is null) return;
        _seenSaved = true;
        var keys = NextWorkLaneLogic.Kept(_snapshot).Select(c => c.TargetKey).ToList();
        _ = _seen.SaveAsync(keys).ContinueWith(t => Console.Error.WriteLine($"kcap: next-work seen keys not saved: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    void RaiseStatus() {
        this.RaisePropertyChanged(nameof(IsAvailable));
        this.RaisePropertyChanged(nameof(IsLoading));
        this.RaisePropertyChanged(nameof(Note));
        this.RaisePropertyChanged(nameof(HasNote));
        this.RaisePropertyChanged(nameof(NoteIsError));
        this.RaisePropertyChanged(nameof(ShowEmpty));
    }

    // ---- holding still under the pointer ----------------------------------------------------------------------

    public void Pin() => _pins++;

    public void Release() {
        if (_pins == 0) return;
        if (--_pins > 0 || _held is not { } held) return;
        _held = null;
        Apply(held);
    }

    // ---- arranging --------------------------------------------------------------------------------------------

    void Arrange() {
        var now      = _time.GetUtcNow();
        var snapshot = _snapshot ?? NextWorkSnapshot.Empty;
        var focus    = NextWorkFocusLogic.Build(snapshot, now);
        IEnumerable<NextWorkFocusItem> all = [.. focus.Waiting, .. focus.Picks, .. focus.More, .. focus.Notes, .. focus.Quiet];
        var newKeys  = NextWorkLaneLogic.NewKeys(all.SelectMany(i => i.Folded.Prepend(i.Row)), _previousKeys);

        var rows = focus.Waiting.Concat(focus.Picks).Take(MaxRows).ToList();
        Cards = [.. rows.Select(i => {
            var card = new NextWorkCardViewModel(this, i, newKeys.Contains(i.Row.TargetKey) || i.Folded.Any(f => newKeys.Contains(f.TargetKey)),
                snapshot.RepoNames, _canLaunch);
            card.IsFilled = string.Equals(i.Row.TargetKey, _filledKey, StringComparison.Ordinal);
            return card;
        })];
        _more = focus.Waiting.Count + focus.Picks.Count - rows.Count + focus.More.Count;
        this.RaisePropertyChanged(nameof(MoreText));
        this.RaisePropertyChanged(nameof(HasMore));
    }

    // ---- row actions ------------------------------------------------------------------------------------------

    /// <summary>Marks the row whose goal the composer holds; null when the composer moved on from it.</summary>
    public void MarkFilled(string? key) {
        _filledKey = key;
        foreach (var card in Cards) card.IsFilled = string.Equals(card.Key, key, StringComparison.Ordinal);
    }

    internal void Open(NextWorkCardViewModel card) {
        if (card.Href is { } href) OpenPath(href);
        else if (NextWorkLaneLogic.RowSessionId(card.Row) is { } session) OpenSession(session);
    }

    void OpenSession(string sessionId) {
        if (_openSession is not null) _openSession(sessionId);
        else OpenPath($"/sessions/{Uri.EscapeDataString(sessionId)}");
    }

    void OpenPath(string pathOrUrl) {
        if (_api.Resolve(pathOrUrl) is { } url) _openUrl?.Invoke(url);
        else _notify?.Invoke("Not signed in to a server");
    }

    /// <summary>A work item or loose end fills the composer with the prompt the server builds for it, in the viewer's
    /// checkout of its repository (this machine's first); a pull request with a goal to review or land it. A session
    /// to resume opens.</summary>
    internal async Task FillAsync(NextWorkCardViewModel card) {
        var row = card.Row;
        if (row.TargetKind is "session" or "evals" || _fill is null) {
            Open(card);
            return;
        }

        card.Busy = true;
        try {
            string? goal = null;
            NextWorkLaunchCheckoutDto? checkout = null;
            if (_canLaunch && NextWorkLaneLogic.IsLaunchable(row)) {
                // A refusal for want of a checkout still carries the prompt: the viewer picks the repository.
                var read = await _api.PrepareLaunchAsync(row.TargetKey, CancellationToken.None);
                if (read is { Kind: NextWorkReadKind.Ready, Value: { } prep }) {
                    goal = prep.Prompt is { Length: > 0 } prompt ? prompt : null;
                    checkout = prep.Refusal is null ? Preferred(prep.Checkouts) : null;
                }
            }

            goal ??= NextWorkLaunchText.Goal(row, card.Label, card.Href is { } href ? _api.Resolve(href) : null);
            var (machine, local, path) = checkout is { } c
                ? (c.DaemonName, string.Equals(c.DaemonName, _localDaemon, StringComparison.Ordinal), c.RepoPath)
                : (null, true, row.RepoHash is { } hash && _localCheckout is not null ? await _localCheckout(hash) : null);
            if (path is not null && machine is null) machine = _localDaemon;

            await _fill(new(row.TargetKey, card.Label, goal, machine, local, path));
            MarkFilled(row.TargetKey);
            if (path is null && row.RepoHash is not null)
                _notify?.Invoke($"No checkout of {Repo(row)} is known on your machines: pick its repository before you start.");
        } finally {
            card.Busy = false;
        }
    }

    /// <summary>Starts an agent in this machine's checkout through the server, with the harness the composer has
    /// chosen when that checkout offers it. Without a checkout here, the row fills the composer instead.</summary>
    internal async Task StartNowAsync(NextWorkCardViewModel card) {
        if (!card.CanWorkOn) {
            await FillAsync(card);
            return;
        }

        card.Busy = true;
        try {
            var read = await _api.PrepareLaunchAsync(card.Key, CancellationToken.None);
            if (read is not { Kind: NextWorkReadKind.Ready, Value: { } prep }) {
                _notify?.Invoke(NextWorkLaunchText.Prepare(read.Kind == NextWorkReadKind.Unavailable ? "unavailable" : null));
                return;
            }
            var here = prep.Refusal is null
                ? (prep.Checkouts ?? []).FirstOrDefault(c => string.Equals(c.DaemonName, _localDaemon, StringComparison.Ordinal))
                : null;
            if (here is null) {
                if (prep.Refusal is { } refusal and not "no_matching_daemon") {
                    _notify?.Invoke(NextWorkLaunchText.Prepare(refusal));
                    return;
                }
                card.Busy = false;
                await FillAsync(card);
                return;
            }

            var vendors = here.Vendors ?? [];
            var vendor = vendors.FirstOrDefault(v => string.Equals(v, _preferredVendor?.Invoke(), StringComparison.OrdinalIgnoreCase))
                ?? vendors.FirstOrDefault(v => v == "claude") ?? vendors.FirstOrDefault();
            if (vendor is null) {
                _notify?.Invoke("No harness on this machine can host an agent.");
                return;
            }

            var launch = await _api.LaunchAsync(new(card.Key, here.DaemonName, here.RepoPath, vendor, null, here.DaemonOwnerUserId), CancellationToken.None);
            switch (launch) {
                case { Kind: NextWorkReadKind.Ready, Value: { Outcome: "requested", AgentId: { } agentId } }:
                    _launched?.Invoke(agentId);
                    _ = RefreshAsync();
                    break;
                case { Kind: NextWorkReadKind.Ready, Value: { Outcome: "pending" } }:
                    _notify?.Invoke("The work is reserved but the launch was not confirmed. Check the sidebar before starting another agent.");
                    break;
                case { Kind: NextWorkReadKind.Ready, Value: { } result }:
                    _notify?.Invoke(NextWorkLaunchText.Start(result.Refusal));
                    break;
                default:
                    _notify?.Invoke(NextWorkLaunchText.Start(null));
                    break;
            }
        } finally {
            card.Busy = false;
        }
    }

    NextWorkLaunchCheckoutDto? Preferred(IReadOnlyList<NextWorkLaunchCheckoutDto>? checkouts) =>
        checkouts is not { Count: > 0 } all ? null
        : all.FirstOrDefault(c => string.Equals(c.DaemonName, _localDaemon, StringComparison.Ordinal)) ?? all[0];

    string Repo(NextWorkRow row) =>
        row.RepoHash is { } h && (_snapshot?.RepoNames ?? NextWorkSnapshot.Empty.RepoNames).TryGetValue(h, out var name) ? name : "this repository";

    internal void Dismiss(NextWorkCardViewModel card) =>
        _ = MutateAsync(() => _api.DismissAsync(card.Key, card.Row.RepoHash, CancellationToken.None), "Couldn't dismiss the suggestion.");

    internal void CloseLooseEnd(string looseEndId) =>
        _ = MutateAsync(() => _api.CloseLooseEndAsync(looseEndId, CancellationToken.None), "Couldn't mark the loose end done.");

    /// <summary><paramref name="choice"/> is <c>tomorrow</c>, <c>3days</c> or <c>nextweek</c>.</summary>
    internal void Snooze(string looseEndId, string choice) {
        var now  = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        DateTimeOffset? until = choice switch {
            "tomorrow" => NextWorkSnoozeTimes.Tomorrow(now, zone),
            "3days"    => NextWorkSnoozeTimes.InThreeDays(now),
            "nextweek" => NextWorkSnoozeTimes.NextWeek(now, zone),
            _          => null,
        };
        if (until is not { } at) return;
        _ = MutateAsync(() => _api.DeferLooseEndAsync(looseEndId, at, CancellationToken.None), "Couldn't snooze the loose end.");
    }

    /// <summary>One change at a time; each reruns the engine, so the list reloads after it. A pinned list applies the
    /// result when the pointer leaves, as any other refresh.</summary>
    async Task MutateAsync(Func<Task<bool>> change, string failure) {
        if (Mutating) return;
        Mutating = true;
        try {
            if (!await change()) _notify?.Invoke(failure);
        } finally {
            Mutating = false;
        }
        await RefreshAsync();
    }
}
