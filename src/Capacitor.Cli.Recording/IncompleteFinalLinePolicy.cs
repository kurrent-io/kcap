namespace Capacitor.Cli.Recording;

/// <summary>What a drain does with a final transcript line that has no newline yet.</summary>
public enum IncompleteFinalLinePolicy {
    /// <summary>Hold it: the agent is mid-write, and consuming a truncated prefix would drop the completed
    /// line for good. Every live drain does this.</summary>
    Hold,

    /// <summary>The final drain's policy: consume it only if the exact bytes read parse as a complete JSON
    /// record. The check runs on the bytes consumed, so a line that resumed growing is still held.</summary>
    ConsumeIfComplete
}
