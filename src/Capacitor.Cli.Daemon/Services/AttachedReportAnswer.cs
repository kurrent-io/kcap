namespace Capacitor.Cli.Daemon.Services;

/// <summary>What the server made of a report about a dispatch sent to an attached agent.</summary>
internal enum AttachedReportAnswer {
    /// <summary>It may not have arrived.</summary>
    Unreached,

    Taken,

    /// <summary>Taken, and recorded nowhere: the agent it named holds no such dispatch.</summary>
    NotRecorded,

    /// <summary>The server answered with an error.</summary>
    Refused,
}
