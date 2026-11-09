namespace Capacitor.Cli.Daemon.Services;

/// <summary>Splits a queueing runtime's write acknowledgement into the two answers
/// <see cref="IHostedAgentRuntime.SendDispatchedInputAsync"/> gives.</summary>
internal static class InputAdmission {
    /// <summary>A refusal faults the acknowledgement before this returns, so one already faulted with
    /// <see cref="InputNotAdmittedException"/> is the input not being taken; anything else is its write.</summary>
    public static Task<Task> Of(Task written) =>
        written is { IsFaulted: true, Exception.InnerException: InputNotAdmittedException refused }
            ? Task.FromException<Task>(refused)
            : Task.FromResult(written);
}
