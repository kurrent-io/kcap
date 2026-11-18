namespace Capacitor.Cli.Recording;

/// <summary>What a stopping recorder does with the session end it would post.</summary>
public enum SessionEndAction {
    Post,

    /// <summary>The session's hooks claim a new process; the end belongs to that one.</summary>
    RunsOnElsewhere,

    /// <summary>The daemon is stopping the hosted agent for resume, which continues the session.</summary>
    HeldForResume
}
