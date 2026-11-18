using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Takes this root's server down for good before anything of the installation is deleted: its own unit
/// stopped and disabled, and removed too when asked, a unit serving another root left alone, and nothing of this
/// root's stack alive.</summary>
static class LocalStackStop {
    /// <summary>Throws <see cref="LocalLifecycleException"/> naming what is left when the stop cannot be verified;
    /// nothing has been deleted then.</summary>
    public static async Task RunAsync(LocalRoot root, LocalServerUnit unit, ILocalServerService service, UserHome home, bool removeUnit,
        CancellationToken ct) {
        using (await LocalUnitLock.AcquireAsync(home, LocalUnitLock.Wait, TimeProvider.System, ct)) {
            switch (LocalRootIdentity.Decide(root, service.Owner(unit))) {
                case OwnerDecision.Ours:
                    FaultPoints.Hit("uninstall:unit");

                    try {
                        if (removeUnit) await service.UninstallAsync(unit, CancellationToken.None);
                        else await service.StopAsync(unit, CancellationToken.None);
                    } catch (Exception ex) when (ex is not LocalLifecycleException) {
                        throw new LocalLifecycleException(
                            $"The {service.Describe} {service.Name(unit)} could not be unloaded ({ex.Message}). Nothing was removed.", ex);
                    }

                    if (removeUnit && Path.GetDirectoryName(service.UnitFile(unit)) is { } unitDir && Directory.Exists(unitDir))
                        DurableDirectory.Sync(unitDir);

                    break;

                case OwnerDecision.NotOurs:
                    // A unit serving another root cannot start this one, and is never stopped to prove it.
                    break;

                default:
                    throw new LocalLifecycleException(
                        $"It cannot be told which installation the {service.Describe} {service.Name(unit)} ({service.UnitFile(unit)}) belongs to, so it was left alone. Nothing was removed.");
            }
        }

        if (LocalStackProbe.FindLive(root) is { } live)
            throw new LocalLifecycleException($"The local server is stopped, but {live} under {root.Directory}. Nothing was removed.");
    }

    /// <summary>Removes this root's unit after asking the server to stop, without waiting past what <paramref name="left"/>
    /// reports: the lock and the stop share that one budget. A unit serving another root is left alone, as in
    /// <see cref="RunAsync"/>.</summary>
    /// <returns>Whether nothing of this root's stack is alive any more.</returns>
    public static async Task<bool> RunWithinAsync(LocalRoot root, LocalServerUnit unit, ILocalServerService service, UserHome home, Func<TimeSpan> left,
        CancellationToken ct) {
        using (await LocalUnitLock.AcquireAsync(home, left(), TimeProvider.System, ct)) {
            switch (LocalRootIdentity.Decide(root, service.Owner(unit))) {
                case OwnerDecision.Ours:
                    try {
                        await service.UninstallWithinAsync(unit, left(), CancellationToken.None);
                    } catch (Exception ex) when (ex is not LocalLifecycleException) {
                        throw new LocalLifecycleException(
                            $"The {service.Describe} {service.Name(unit)} could not be unloaded ({ex.Message}). Nothing was removed.", ex);
                    }

                    break;

                case OwnerDecision.NotOurs:
                    break;

                default:
                    throw new LocalLifecycleException(
                        $"It cannot be told which installation the {service.Describe} {service.Name(unit)} ({service.UnitFile(unit)}) belongs to, so it was left alone. Nothing was removed.");
            }
        }

        return LocalStackProbe.FindLive(root) is null;
    }

    public static readonly TimeSpan DeleteRetryGap = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the files of a server that passed every stop check may stay held before the deletion gives up.</summary>
    static readonly TimeSpan ReleaseBudget = TimeSpan.FromSeconds(15);

    /// <summary>Runs <paramref name="delete"/> again while it is refused and <paramref name="retry"/> allows. On Windows a
    /// binary stays held for a moment after the server that ran it has exited, past every check a stop makes.</summary>
    /// <returns>Null once it succeeds, or the refusal that ended the retries.</returns>
    public static async Task<Exception?> DeleteWhenReleasedAsync(Action delete, Func<bool> retry, TimeProvider time, CancellationToken ct) {
        while (true) {
            try {
                delete();
                return null;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                if (!retry()) return ex;
            }

            await Task.Delay(DeleteRetryGap, time, ct);
        }
    }

    /// <summary><see cref="DeleteWhenReleasedAsync(Action, Func{bool}, TimeProvider, CancellationToken)"/> for up to
    /// <see cref="ReleaseBudget"/>; throws <see cref="LocalLifecycleException"/> telling the user to run
    /// <paramref name="rerun"/> once it runs out.</summary>
    public static async Task DeleteWhenReleasedAsync(LocalRoot root, Action delete, string rerun, TimeProvider time, CancellationToken ct) {
        var started = time.GetTimestamp();

        if (await DeleteWhenReleasedAsync(delete, () => time.GetElapsedTime(started) < ReleaseBudget, time, ct) is { } held)
            throw new LocalLifecycleException(
                $"The local server is stopped, but a file under {root.Directory} is still in use ({held.Message}). Run {rerun} again.", held);
    }

    /// <summary>Removes each named directory under the root, a link without following it, then syncs the root.</summary>
    public static void Delete(LocalRoot root, params string[] names) {
        foreach (var name in names) {
            var path = Path.Combine(root.Directory, name);
            if (!LocalTree.Remove(path)) continue;

            FaultPoints.Hit($"unlink:{path}");
        }

        DurableDirectory.Sync(root.Directory);
    }
}
