using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// What a rail row's context menu runs. The menu offers an item only where it can work; the server and
/// the daemon still make the decision.
public sealed class RailSessionActions(
        Action<string, AgentOrigin> rename,
        Func<string, CancellationToken, Task<ProcessResult>> hide,
        Func<string, CancellationToken, Task<ProcessResult>> disable,
        Func<string, Task<bool>> confirmDelete,
        Action<string, string, string, AgentOrigin> stop,
        IAppNotifier notifier,
        Func<Task>? refresh,
        CancellationToken shutdown) : IRailSessionActions {
    /// A recorded session the signed-in user owns: a local daemon's rows are this machine's account,
    /// any other row needs the listing's word.
    public static bool OwnsSession(AgentRow row) =>
        row is { Origin: not AgentOrigin.Pending, IsPlaceholder: false, SessionId.Length: > 0 }
        && (row.Origin == AgentOrigin.Local || row.OwnerIsViewer == true);

    /// `kcap disable` stops only this machine's recorders, and a send that lands after the deletion brings rows
    /// back, so a session still live on another machine is not offered for deletion until it ends.
    public static bool CanDelete(AgentRow row) =>
        OwnsSession(row) && (row.Origin == AgentOrigin.Local || row.IsEndedSession);

    /// A running agent a daemon hosts for the signed-in user; a terminal session no daemon hosts has nothing to stop.
    public static bool CanTerminate(AgentRow row) =>
        row is {
            Origin: AgentOrigin.Local or AgentOrigin.Remote or AgentOrigin.Pending,
            IsPlaceholder: false,
            Status: not ("Completed" or "Failed" or AgentRow.DaemonDownStatus)
        }
        && (row.Origin != AgentOrigin.Remote || row.OwnerIsViewer == true);

    public void Rename(AgentRow row) {
        if (!OwnsSession(row)) return;
        rename(row.Id, row.Origin);
    }

    public async Task MakePrivateAsync(AgentRow row, string label) {
        if (!OwnsSession(row)) return;
        var result = await RunAsync(hide, row.SessionId!).ConfigureAwait(false);
        notifier.Notify(result is { ExitCode: 0, TimedOut: false } ? $"{label} is now private" : Failure($"Couldn't make {label} private", result).TrimEnd('.'));
    }

    public async Task DeleteAsync(AgentRow row, string label) {
        if (!CanDelete(row) || !await confirmDelete(label).ConfigureAwait(false)) return;
        var result = await RunAsync(disable, row.SessionId!).ConfigureAwait(false);
        notifier.Notify(DeleteOutcome(label, result).TrimEnd('.'));
        if (refresh is not null) {
            try {
                await refresh().ConfigureAwait(false);
            } catch (Exception) {
                // The listing's next read drops the row anyway.
            }
        }
    }

    public void Terminate(AgentRow row, string label) {
        if (!CanTerminate(row)) return;
        stop(row.Id, label, row.Kind, row.Origin == AgentOrigin.Remote ? AgentOrigin.Remote : AgentOrigin.Local);
    }

    async Task<ProcessResult?> RunAsync(Func<string, CancellationToken, Task<ProcessResult>> run, string sessionId) {
        try {
            return await run(sessionId, shutdown).ConfigureAwait(false);
        } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) {
            return null;
        } catch (Exception ex) {
            return new ProcessResult(1, "", ex.Message, false);
        }
    }

    /// `kcap disable` exits 0 with its local steps done and the server unreached, and 1 with the data deleted but a
    /// doubt left, so its own lines, which say only what the run confirmed, are what the user is told.
    internal static string DeleteOutcome(string label, ProcessResult? result) {
        if (result is null || result.TimedOut) return Failure($"Couldn't delete {label}", result);
        var lines = Lines(result.Stdout);
        var deleted = result.ExitCode == 0 && lines.FirstOrDefault() is { } summary
            && !summary.Contains("not deleted", StringComparison.Ordinal);
        if (deleted) return string.Join(" ", [$"Deleted {label} from kcap.", .. lines.Skip(1)]);
        if (lines.Length == 0) return Failure($"Couldn't delete {label}", result);
        return string.Join(" ", [$"{label}:", .. lines, .. Lines(result.Stderr).Take(1)]);
    }

    static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string Failure(string what, ProcessResult? result) {
        if (result is null) return what;
        if (result.TimedOut) return $"{what}: kcap did not answer in time";
        var detail = Lines(result.Stderr).FirstOrDefault() ?? Lines(result.Stdout).FirstOrDefault();
        return detail is null ? what : $"{what}: {detail}";
    }
}
