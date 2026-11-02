using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Moves this user's local server to <c>kcap-service</c> when the service is installed, and back to a <c>kcap-local</c>
/// task before it is removed. Each move holds the lifecycle lease and the unit lock, keeps a stopped server stopped,
/// and ends with exactly one supervisor: one that cannot finish puts the task back.
///
/// <para>A running server is restarted, not handed over: the task's server lives on the task's own console, which
/// ending the task closes, and a task cannot take over a process it did not start. So a move waits, still under the
/// lease, until the restarted server answers ready, and says how long it was down.</para>
/// </summary>
static class LocalServerMigration {
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    /// <summary>A service installed a moment ago may not have opened its pipe yet.</summary>
    static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(10);

    [SupportedOSPlatform("windows")]
    public static async Task<LocalServerMove?> ToServiceAsync(LocalRoot root, UserHome home, TimeProvider time) {
        var pipe        = new WindowsServicePipeClient(time);
        var enrollments = new RegistryLocalServerEnrollmentStore();
        var task        = new LocalServerTaskService(home, time);

        using var http = ReadinessClient();

        return await ToServiceAsync(root, home, time, task, new LocalServerWindowsService(home, time, pipe, enrollments, task), enrollments,
            () => ServicePipeVerbs.ServesLocalServer(pipe, ServiceWait), WaitReady(http, time), CancellationToken.None);
    }

    [SupportedOSPlatform("windows")]
    public static async Task<LocalServerMove?> ToTaskAsync(UserHome home, TimeProvider time) {
        var enrollments = new RegistryLocalServerEnrollmentStore();
        var task        = new LocalServerTaskService(home, time);

        using var http = ReadinessClient();

        return await ToTaskAsync(home, time, task, new LocalServerWindowsService(home, time, new WindowsServicePipeClient(time), enrollments, task),
            enrollments, WaitReady(http, time), CancellationToken.None);
    }

    /// <summary>The local installation's lane: no credential, no proxy, no redirect.</summary>
    static HttpClient ReadinessClient() =>
        new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };

    static Func<LocalServerUnit, CancellationToken, Task> WaitReady(HttpClient http, TimeProvider time) =>
        (unit, ct) => LocalServerReadiness.WaitAsync(http, unit, LocalServerReadiness.StartBudget, time, ct);

    /// <summary>Best effort: throws only on cancellation, and leaves a server it could not move under its task.</summary>
    /// <param name="waitReady">Returns once the server answers ready; throws <see cref="LocalLifecycleException"/> when it
    /// does not in time.</param>
    /// <returns>What to tell the user, or null when there was nothing to move.</returns>
    public static async Task<LocalServerMove?> ToServiceAsync(LocalRoot root, UserHome home, TimeProvider time, ILocalServerService task,
        LocalServerWindowsService service, ILocalServerEnrollmentStore enrollments, Func<bool> serviceRunsServers,
        Func<LocalServerUnit, CancellationToken, Task> waitReady, CancellationToken ct) {
        if (enrollments.Read() is not null || Installed(root) is not { } installation) return null;

        var unit = LocalServerUnit.ForInstallation(root, installation.Port);
        if (!File.Exists(task.UnitFile(unit))) return null;

        if (!serviceRunsServers())
            return new("kcap-service cannot run the local server, so it stays a Scheduled Task.");

        var running = false;
        DateTimeOffset? down = null;

        try {
            using var lease = await LifecycleLease.AcquireAsync(root, LockWait, time, ct);
            if (lease.ReadJournal() is StateRead<LifecycleJournalEntry>.Present(var entry))
                return new($"The local server stays a Scheduled Task: an interrupted {entry.Operation} is pending, which kcap local start finishes.");

            using (await LocalUnitLock.AcquireAsync(home, LockWait, time, ct)) {
                if (LocalRootIdentity.Decide(root, task.Owner(unit)) != OwnerDecision.Ours)
                    return new($"The Scheduled Task {task.Name(unit)} is not provably this installation's, so the local server stays under it.");

                running = task.Activity(unit) is UnitActivity.Running or UnitActivity.Restarting;
                if (running) down = time.GetUtcNow();

                service.Install(unit, start: running);
            }

            var moved = enrollments.Read() is not null;

            if (down is not { } since)
                return moved ? new("Moved the local server from its Scheduled Task to kcap-service; it stays stopped until kcap local start.") : null;

            // A service that refused the enrollment has said so, and handed the server to a new task.
            return await RestartedAsync(
                moved ? "Moved the local server from its Scheduled Task to kcap-service, which restarted it." : "Its Scheduled Task restarted the local server.",
                unit, since, time, waitReady, ct);
        } catch (Exception ex) when (ex is LocalLifecycleException or IOException or UnauthorizedAccessException or InvalidOperationException) {
            var (restored, restarted) = await RestoreTaskAsync(unit, running, task, enrollments);
            var said = $"The local server could not be moved to kcap-service ({ex.Message}).{restored}";

            return restarted && down is { } since ? await RestartedAsync(said, unit, since, time, waitReady, ct) : new(said);
        }
    }

    static async Task<(string Said, bool Restarted)> RestoreTaskAsync(LocalServerUnit unit, bool running, ILocalServerService task,
        ILocalServerEnrollmentStore enrollments) {
        if (enrollments.Read() is not null || task.IsInstalled(unit)) return ("", false);

        try {
            task.Install(unit);
            if (!running) await task.StopAsync(unit, CancellationToken.None);

            return (" Its Scheduled Task runs it again.", running);
        } catch (Exception ex) when (ex is LocalLifecycleException or IOException or UnauthorizedAccessException) {
            return ($" Putting its Scheduled Task back failed too ({ex.Message}); kcap local start installs it again.", false);
        }
    }

    /// <summary>Throws <see cref="LocalLifecycleException"/> when the server cannot be moved; the service must then stay,
    /// or nothing would supervise it. A move whose restarted server does not come back is <see cref="LocalServerMove.Failed"/>.</summary>
    /// <returns>What to tell the user, or null when nothing was enrolled.</returns>
    public static async Task<LocalServerMove?> ToTaskAsync(UserHome home, TimeProvider time, ILocalServerService task, LocalServerWindowsService service,
        ILocalServerEnrollmentStore enrollments, Func<LocalServerUnit, CancellationToken, Task> waitReady, CancellationToken ct) {
        if (enrollments.Read() is not { } enrollment) return null;

        var root = new LocalRoot(enrollment.Root, home);

        if (Installed(root) is not { } installation) {
            await service.UninstallAsync(LocalServerUnit.ForInstallation(root, 0), ct);
            return new("Removed kcap-service's enrollment of a local server that is no longer installed.");
        }

        var unit = LocalServerUnit.ForInstallation(root, installation.Port);

        using var lease = await LifecycleLease.AcquireAsync(root, LockWait, time, ct);
        if (lease.ReadJournal() is StateRead<LifecycleJournalEntry>.Present(var entry))
            throw new LocalLifecycleException($"an interrupted {entry.Operation} of the local installation is pending; run kcap local start to finish it first");

        DateTimeOffset down;

        using (await LocalUnitLock.AcquireAsync(home, LockWait, time, ct)) {
            down = time.GetUtcNow();

            try {
                await service.UninstallAsync(unit, ct);
            } catch (LocalLifecycleException) {
                // The service is staying, and its server must not stay stopped for a move that did not happen.
                if (enrollments.Read() is not null) enrollments.SetStopped(enrollment.Stopped);
                throw;
            }

            try {
                task.Install(unit);
                if (enrollment.Stopped) await task.StopAsync(unit, ct);
            } catch (Exception ex) when (ex is LocalLifecycleException or IOException or UnauthorizedAccessException) {
                throw new LocalLifecycleException(
                    $"the local server was stopped and taken off kcap-service, but its Scheduled Task could not be installed ({ex.Message}); kcap local start installs it");
            }
        }

        if (enrollment.Stopped) return new("Moved the local server from kcap-service to a Scheduled Task; it stays stopped until kcap local start.");

        return await RestartedAsync("Moved the local server from kcap-service to a Scheduled Task, which restarted it and runs it while you are signed in.",
            unit, down, time, waitReady, ct);
    }

    /// <summary>A move is reported done only once its server answers ready again.</summary>
    static async Task<LocalServerMove> RestartedAsync(string said, LocalServerUnit unit, DateTimeOffset down, TimeProvider time,
        Func<LocalServerUnit, CancellationToken, Task> waitReady, CancellationToken ct) {
        try {
            await waitReady(unit, ct);
        } catch (Exception ex) when (ex is LocalLifecycleException or IOException or UnauthorizedAccessException) {
            return new($"{said} It did not come back: {ex.Message}", Failed: true);
        }

        return new($"{said} It was down for {Seconds(time.GetUtcNow() - down)} and is ready again on {unit.BaseUrl}.");
    }

    static string Seconds(TimeSpan span) {
        var seconds = Math.Max(0, (int)Math.Round(span.TotalSeconds));
        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }

    static LocalInstallation? Installed(LocalRoot root) =>
        LocalStateFiles.ReadRegistry(root) is StateRead<InstallRegistry>.Present { Value.Active: { } active } ? active : null;
}
