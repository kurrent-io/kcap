namespace Capacitor.Cli.Core.Workspace;

/// <summary>Why a workspace spool drain ended.</summary>
public enum WorkspaceDrainStop {
    Drained,
    /// <summary>The server is marked as lacking the route; nothing was sent.</summary>
    Paused,
    Budget,
    /// <summary>A failure a later attempt may get past; the file is kept.</summary>
    Retryable,
    /// <summary>A post and its probe both answered 404; the server is now paused.</summary>
    RouteMissing,
    /// <summary>A post answered 404 and the probe settled nothing; the file is kept, nothing is paused.</summary>
    ProbeUnsettled,
}
