namespace Capacitor.Cli.Commands;

/// <summary><see cref="Running"/> means the child said it accepted its work; <see cref="Unconfirmed"/>
/// means it was still alive without saying so when setup stopped waiting, which is neither.</summary>
internal enum BackgroundImportStatus { NotNeeded, Running, ExitedZero, Failed, Unconfirmed }

internal static class BackgroundImportStatusExtensions {
    public static string Wire(this BackgroundImportStatus status) => status switch {
        BackgroundImportStatus.NotNeeded   => "not_needed",
        BackgroundImportStatus.Running     => "running",
        BackgroundImportStatus.ExitedZero  => "exited_zero",
        BackgroundImportStatus.Failed      => "failed",
        BackgroundImportStatus.Unconfirmed => "unconfirmed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
