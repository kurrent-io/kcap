namespace Capacitor.Cli.Daemon.Services;

/// <summary>Who wrote a message to a hosted session's terminal, which is what the row it becomes is stamped with</summary>
internal readonly record struct InputAuthor {
    InputAuthor(Guid? dispatchId, string? agentId, bool isOwner) {
        DispatchId = dispatchId;
        AgentId    = agentId;
        IsOwner    = isOwner;
    }

    public Guid? DispatchId { get; }

    /// <summary>The agent a dispatch was sent to, whose input stream the server reads it from</summary>
    public string? AgentId { get; }

    public bool IsOwner { get; }

    /// <summary>A message the server dispatched to <paramref name="agentId"/></summary>
    public static InputAuthor Dispatch(Guid dispatchId, string agentId) => new(dispatchId, agentId, false);

    /// <summary>The session's owner, at a local surface such as the desktop composer</summary>
    public static InputAuthor Owner { get; } = new(null, null, true);

    /// <summary>The daemon's own text, such as a resume nudge or a compaction: its row consumes its entry and takes no
    /// stamp</summary>
    public static InputAuthor Daemon { get; } = new(null, null, false);
}
