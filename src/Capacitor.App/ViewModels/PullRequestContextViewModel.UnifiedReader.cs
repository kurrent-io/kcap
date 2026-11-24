using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

public sealed partial class PullRequestContextViewModel {
    UnifiedPullRequestPage? _unifiedPage;
    bool _usesUnifiedReader;
    bool UnifiedReaderVisible => _usesUnifiedReader && _readerVisible;

    internal UnifiedPullRequestPage? SetUnifiedReader(bool enabled) {
        if (_disposed) return null;
        if (_usesUnifiedReader == enabled) return enabled ? _unifiedPage : null;
        _usesUnifiedReader = enabled;
        if (enabled) {
            if (_unifiedPage is null) {
                _unifiedPage = new(this);
                if (_section is not ("overview" or "thread_comments")) _unifiedPage.RememberShortcut(_section);
            }
            InvalidateUnifiedHead(_overview?.HeadSha);
        }
        else _unifiedPage?.ReleasePages();
        Notify();
        if (enabled) _unifiedPage?.LoadVisited();
        if (!enabled && _readerVisible && CanReveal && ReadsRows) LoadOrRefreshSection();
        return enabled ? _unifiedPage : null;
    }

    internal bool UnifiedCanDisplay => UnifiedReaderVisible && CanNavigate;
    internal bool UnifiedCanRead => UnifiedCanDisplay && CanReveal && !UnifiedCoolingDown;
    internal bool UnifiedCoolingDown => ReadRetryAt > _time.GetUtcNow().UtcDateTime;
    internal string? UnifiedDescription => UnifiedCanDisplay ? _overview?.Description : null;
    internal bool UnifiedDescriptionTruncated => UnifiedCanDisplay && _overview?.DescriptionTruncated == true;
    internal string? UnifiedStaleReason => _staleReason;
    internal bool UnifiedConflictDetails => HasConflicts || _overview is { Lifecycle: "open" or "draft", Mergeable: null };
    internal PullRequestStatus UnifiedConflictStatus => HasConflicts ? ConflictsStatus
        : _overview?.Mergeable is true ? new("No merge conflicts", "success", ConflictsNote)
        : new("Mergeability unknown", Detail: ConflictsNote);
    internal PullRequestStatus UnifiedChecksStatus =>
        _unifiedHeadRestarts?.Contains("checks") == true || _overview?.Checks?.HeadSha is { } summaryHead && summaryHead != _overview.HeadSha
            ? new("Checks updating for this commit") : ChecksStatus;
    internal PullRequestStatus UnifiedThreadsStatus => HasThreadsStatus ? ThreadsStatus
        : _overview?.Threads?.Availability.Status == "ready" ? new("Unresolved threads unknown")
        : new("Thread status unavailable");

    internal PullRequestSectionState? UnifiedSection(string key) => UnifiedCanDisplay ? _sections.GetValueOrDefault(key) : null;
    internal bool UnifiedPagePending(string key) => _pageRequests.Contains(key);

    internal void LoadUnifiedPage(string section, string? filter, string? thread, string? cursor = null, bool refresh = false, bool earlier = false) {
        if (UnifiedCanRead) RequestPage(section, filter, thread, cursor, refresh, earlier);
    }

    internal void SaveUnifiedPages() {
        if (_usesUnifiedReader && CanReveal && _selected is { } choice && _sections.Count > 0)
            _unifiedPage?.SavePages(choice.Subject, _sections, OverviewAccount(_overviewRead));
    }

    internal void RestoreUnifiedPages() {
        if (!_usesUnifiedReader || _selected is not { } choice || _unifiedPage?.TakePages(choice.Subject) is not { } cached) return;
        foreach (var (key, state) in cached) _sections[key] = state;
    }

    internal void TrimUnifiedPageCache(long activeBytes) => _unifiedPage?.TrimPages(activeBytes);

    static (string? Reader, string? Account) OverviewAccount(PullRequestRead<PullRequestOverviewDto>? read) =>
        (read?.Reader, read?.Data?.AccessCheckedFor);

    void ValidateUnifiedOverview(PullRequestRead<PullRequestOverviewDto> read) {
        if (!_usesUnifiedReader) return;
        var account = OverviewAccount(read);
        if (_overviewRead is not null && OverviewAccount(_overviewRead) != account) {
            ResetUnifiedReader();
            _sections.Clear();
        } else if (_unifiedPage?.AcceptCachedAccount(account) == false) {
            _sections.Clear();
        }
        InvalidateUnifiedHead(read.Data?.HeadSha);
    }

    void InvalidateUnifiedHead(string? head) {
        if (!_usesUnifiedReader) return;
        foreach (var (key, state) in _sections.ToArray()) {
            if ((key == "checks" || state.Head is not null) && state.Head != head) _sections.Remove(key);
        }
    }

    bool IsUnifiedReadCurrent(string key, string? head) =>
        (key != "checks" && head is null) || head == _overview?.HeadSha;

    void ResetUnifiedReader() => _unifiedPage?.Reset();
}
