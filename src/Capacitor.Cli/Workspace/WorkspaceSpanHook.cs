using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Workspace;

namespace Capacitor.Cli.Workspace;

/// <summary>
/// The span work a hook does at a turn or session boundary: open or close the session's workspace spans
/// against this process's server, then drain the workspace spool once the span lock is released. Only a
/// main session takes spans — a subagent runs inside its parent's turn. Never throws.
/// </summary>
sealed class WorkspaceSpanHook(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http, TimeProvider time, TextWriter? stderr = null) {
    /// <summary>What a session-start may spend on its span, ahead of its lifecycle post.</summary>
    public static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(2);

    /// <summary>What a session end may spend closing spans and draining, after its lifecycle end.</summary>
    public static readonly TimeSpan EndBudget = TimeSpan.FromSeconds(4);

    /// <summary>The longest a drain may run after the span work.</summary>
    static readonly TimeSpan DrainCap = TimeSpan.FromSeconds(1.5);

    string? Url => profiles.Resolution.ServerUrl;

    TextWriter Stderr => stderr ?? Console.Error;

    internal WorkspaceBounds Bounds { get; init; } = WorkspaceBounds.Default;

    internal Func<Task<AuthAttempt>>? ClientFactory { get; init; }

    WorkspaceSpool Spool => field ??= new WorkspaceSpool(config, Url, time, Stderr.WriteLine);

    WorkspaceSpanStore Store => field ??= new WorkspaceSpanStore(config, Url, Spool, new WorkspaceSnapshotter(new WorkspaceGit(time), time, Bounds), time);

    /// <summary>The store for one piece of span work. Its lock waits shrink with the budget, so a short
    /// hook's span work stays inside what it was given.</summary>
    WorkspaceSpanStore StoreWithin(TimeSpan budget) {
        var quarter = budget / 4;
        return new WorkspaceSpanStore(config, Url, Spool, new WorkspaceSnapshotter(new WorkspaceGit(time), time, Bounds), time) {
            LockWait = quarter < WorkspaceSpanLock.DefaultWait ? quarter : WorkspaceSpanLock.DefaultWait,
        };
    }

    bool Withheld(string sessionId) => DisabledSessions.IsDisabled(sessionId, config);

    public Task OpenAsync(string? sessionId, string? dir, WorkspaceOpen mode, TimeSpan budget, bool drain = true) =>
        RunAsync(sessionId, budget, drain, left => dir is null ? Task.CompletedTask : StoreWithin(budget).OpenAsync(sessionId!, dir, mode, left()));

    public Task CloseAsync(string? sessionId, TimeSpan budget, bool drain = true) =>
        RunAsync(sessionId, budget, drain, left => StoreWithin(budget).CloseAllAsync(sessionId!, left()));

    /// <summary>Closes the session's spans and deletes its baselines. A failed post stays in the spool.
    /// Callers run it after the session's own end is handed off or posted, which must never wait on it.</summary>
    public Task EndAsync(string? sessionId, TimeSpan budget, bool drain = true) =>
        RunAsync(sessionId, budget, drain, left => StoreWithin(budget).EndSessionAsync(sessionId!, left()));

    /// <summary>Whether anything of the session's spans is left after <see cref="EndAsync"/>.</summary>
    public bool Holds(string sessionId) => Store.Holds(sessionId);

    /// <summary>Drops the session's open spans and spooled events unsent, and says whether none are left. A
    /// session end clears the disabled marker only once this succeeds: the marker is what withholds them,
    /// and every later hook and drain retries the discard while it stands.</summary>
    public bool Discard(string? sessionId) {
        if (string.IsNullOrEmpty(sessionId)) return true;
        try {
            var spans   = Store.Discard(sessionId);
            var spooled = Spool.Discard(sessionId);
            return spans && spooled;
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            Stderr.WriteLine($"[kcap] workspace span discard failed for {sessionId}: {ex.Message}");
            return false;
        }
    }

    public async Task DrainAsync(TimeSpan budget) {
        try {
            await DrainCoreAsync(budget);
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            Stderr.WriteLine($"[kcap] workspace spool drain failed: {ex.Message}");
        }
    }

    async Task RunAsync(string? sessionId, TimeSpan budget, bool drain, Func<Func<TimeSpan>, Task> work) {
        if (string.IsNullOrEmpty(sessionId)) return;

        var started = time.GetTimestamp();
        TimeSpan Left() => budget - time.GetElapsedTime(started);

        try {
            if (Withheld(sessionId)) {
                Store.Discard(sessionId);
                Spool.Discard(sessionId);
            } else {
                await work(Left);
            }
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            Stderr.WriteLine($"[kcap] workspace span failed for {sessionId}: {ex.Message}");
        }

        // After the span's own work, so maintenance never delays the turn's snapshot.
        try {
            Store.Prune();
            Spool.DeleteExpired();
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            Stderr.WriteLine($"[kcap] workspace span cleanup failed: {ex.Message}");
        }

        if (drain) await DrainAsync(Left());
    }

    async Task DrainCoreAsync(TimeSpan budget) {
        if (budget <= TimeSpan.Zero || !HookHttp.IsPostable(Url) || Spool.IsPaused || !Spool.HasPending) return;

        var window  = budget < DrainCap ? budget : DrainCap;
        var started = time.GetTimestamp();
        var created = await BoundedAuth.CreateClientWithinAsync(ClientFactory ?? (() => http.ForHookAsync()), window, time);
        if (created is not { } attempt) return;

        using var client = attempt.Client;
        if (!attempt.Usable) return;

        await Spool.DrainAsync(client, Url!, window - time.GetElapsedTime(started), withheld: Withheld);
    }
}
