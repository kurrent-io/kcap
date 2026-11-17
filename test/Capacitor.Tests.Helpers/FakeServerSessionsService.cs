using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Client;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.Tests.Helpers;

/// Scripted IServerSessionsService: caches and subjects a test edits directly, and recorded calls.
public sealed class FakeServerSessionsService : IServerSessionsService, IDisposable {
    public SourceCache<RepoSessionDto, string> LiveCache { get; } = new(r => r.SessionId);
    public SourceCache<RepoSessionDto, string> MineLiveCache { get; } = new(r => r.SessionId);
    public SourceCache<RepoSessionDto, string> EndedCache { get; } = new(r => r.SessionId);
    public BehaviorSubject<ServerSessionsPaging> PagingSubject { get; } = new(new(0, 0, 1, false, false));
    public BehaviorSubject<int> LiveNotShownSubject { get; } = new(0);
    public BehaviorSubject<IReadOnlySet<string>> ViewerIdsSubject { get; } = new(FrozenSet<string>.Empty);
    public BehaviorSubject<string?> ViewerUserIdSubject { get; } = new(null);
    public BehaviorSubject<bool> CurrentSubject { get; } = new(true);
    readonly BehaviorSubject<RailScope> _scope = new(RailScope.Mine);
    public BehaviorSubject<RepoSessionDto?> PinnedSubject { get; } = new(null);
    public BehaviorSubject<string?> PinnedIdSubject { get; } = new(null);
    public BehaviorSubject<SessionIdentity> IdentitySubject { get; } = new(new(0, 1, true));
    /// What PinAsync answers; a Pinned answer also sets the pinned id.
    public PinOutcome PinResult { get; set; } = PinOutcome.Pinned;
    public List<(string SessionId, PinPresentation Presentation)> PinCalls { get; } = [];
    public int UnpinCalls { get; set; }
    public int ShowMoreCalls { get; set; }
    public int ReconcileCalls { get; set; }

    public IObservableCache<RepoSessionDto, string> Live => LiveCache.AsObservableCache();
    public IObservableCache<RepoSessionDto, string> MineLive => MineLiveCache.AsObservableCache();
    public IObservableCache<RepoSessionDto, string> Ended => EndedCache.AsObservableCache();
    public string? Kept { get; set; }
    public Subject<string> DroppedSubject { get; } = new();
    public IObservable<string> Dropped => DroppedSubject;
    /// Derived from the three caches and the pin subjects, so a test edits one and the directory sees the whole state.
    public IObservable<ServerSessionsSnapshot> Snapshots => Observable.CombineLatest(
        LiveCache.Connect(suppressEmptyChangeSets: false).ToCollection().StartWith((IReadOnlyCollection<RepoSessionDto>)[]),
        MineLiveCache.Connect(suppressEmptyChangeSets: false).ToCollection().StartWith((IReadOnlyCollection<RepoSessionDto>)[]),
        EndedCache.Connect(suppressEmptyChangeSets: false).ToCollection().StartWith((IReadOnlyCollection<RepoSessionDto>)[]),
        PinnedSubject, PinnedIdSubject,
        (live, mine, ended, pinned, pinnedId) => new ServerSessionsSnapshot([.. live], [.. mine], [.. ended], pinned, pinnedId));
    public IObservable<SessionIdentity> Identity => IdentitySubject;
    public string? PinnedSessionId => PinnedIdSubject.Value;

    /// When set, PinAsync answers once the test completes it rather than with PinResult.
    public TaskCompletionSource<PinOutcome>? PinGate { get; set; }

    int _pinRequest;

    /// A later PinAsync or an Unpin supersedes one still waiting on its gate, as the real service does.
    public async Task<PinOutcome> PinAsync(string sessionId, PinPresentation presentation, CancellationToken ct) {
        PinCalls.Add((sessionId, presentation));
        var request = ++_pinRequest;
        var result = PinGate is { } gate ? await gate.Task : PinResult;
        if (request != _pinRequest) return PinOutcome.Superseded;
        if (result == PinOutcome.Pinned) PinnedIdSubject.OnNext(sessionId);
        return result;
    }

    public HandoffContextDto? HandoffContext { get; set; }
    /// Anything but Ok answers without HandoffContext.
    public ServerReadOutcome HandoffContextOutcome { get; set; } = ServerReadOutcome.Ok;
    public Task<HandoffContextFetch> ReadHandoffContextAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(HandoffContextOutcome == ServerReadOutcome.Ok && HandoffContext is { } context
            ? new HandoffContextFetch(ServerReadOutcome.Ok, context)
            : new HandoffContextFetch(HandoffContextOutcome == ServerReadOutcome.Ok ? ServerReadOutcome.Failed : HandoffContextOutcome, null));

    public ActivitySessionDto? Summary { get; set; }
    public TaskCompletionSource? SummaryGate { get; set; }
    public TaskCompletionSource SummaryHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int SummaryReads { get; private set; }
    public async Task<SummaryFetch> ReadSummaryAsync(string sessionId, CancellationToken ct) {
        SummaryReads++;
        if (SummaryGate is { } gate) {
            SummaryHeld.TrySetResult();
            await gate.Task;
        }
        return Summary is { } summary
            ? new SummaryFetch(ServerReadOutcome.Ok, summary)
            : new SummaryFetch(ServerReadOutcome.Failed, null);
    }

    public void Unpin() {
        _pinRequest++;
        UnpinCalls++;
        PinnedSubject.OnNext(null);
        PinnedIdSubject.OnNext(null);
    }
    public void Keep(string? sessionId) => Kept = sessionId;
    public List<(string SessionId, string Title)> Renames { get; } = [];
    public void ApplyRename(string sessionId, string title) => Renames.Add((sessionId, title));
    public IObservable<ServerSessionsPaging> Paging => PagingSubject;
    public IObservable<int> LiveNotShown => LiveNotShownSubject;
    public IObservable<IReadOnlySet<string>> ViewerIds => ViewerIdsSubject;
    public IObservable<string?> ViewerUserId => ViewerUserIdSubject;
    public IObservable<bool> Current => CurrentSubject;
    public RailScope Scope { get => _scope.Value; set { if (_scope.Value != value) _scope.OnNext(value); } }
    public IObservable<RailScope> ScopeChanges => _scope;

    public Task ShowMoreAsync() { ShowMoreCalls++; return Task.CompletedTask; }
    public Task ReconcileAsync() { ReconcileCalls++; return Task.CompletedTask; }

    public void Dispose() {
        LiveCache.Dispose();
        MineLiveCache.Dispose();
        EndedCache.Dispose();
    }
}
