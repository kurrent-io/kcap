using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli.Commands;

/// <summary><see cref="Running"/> means the child said it accepted its work; <see cref="Unconfirmed"/>
/// means it was still alive without saying so when setup stopped waiting, which is neither.</summary>
internal enum BackgroundImportStatus { NotNeeded, Running, ExitedZero, Failed, Unconfirmed }

internal static class BackgroundImportStatusExtensions {
    public static string Wire(this BackgroundImportStatus status) => status switch {
        BackgroundImportStatus.NotNeeded   => FirstRunImportBackground.NotNeeded,
        BackgroundImportStatus.Running     => FirstRunImportBackground.Running,
        BackgroundImportStatus.ExitedZero  => FirstRunImportBackground.ExitedZero,
        BackgroundImportStatus.Failed      => FirstRunImportBackground.Failed,
        // The server refuses an import outcome carrying a status it does not know, and older eval-watch
        // skills a handoff file it does not know; an alive child reported "running" before it had to confirm.
        BackgroundImportStatus.Unconfirmed => FirstRunImportBackground.Running,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
