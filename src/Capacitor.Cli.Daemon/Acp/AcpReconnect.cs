namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Provide reconnect support only for a verified vendor on an interactive launch with reconnect
/// enabled. Without it, child death ends the read stream and finalizes the runtime.
/// </summary>
internal sealed class AcpReconnectSupport {
    /// <summary>
    /// The pure spawn closure: constructs a fresh child process + stdio streams for the
    /// SAME launch shape as the original (binary, argv, env, cwd) and nothing else — no agent
    /// registration, no forwarder, no slot accounting. The factory closes this over the launch's
    /// <c>RuntimeStartContext</c> and its own connection source, so candidates and the original
    /// child are spawned by the same code path.
    /// </summary>
    public required Func<(Stream Input, Stream Output, IAcpProcess Process)> Spawn { get; init; }

    /// <summary>Drives attempt backoff and the settlement-wait bound. The same provider the runtime
    /// this support object is handed to was given, or one incident is bounded on two clocks.</summary>
    public required TimeProvider TimeProvider { get; init; }

    /// <summary>
    /// Publish Record and Clear as one immutable bundle so cleanup cannot observe partial wiring.
    /// Record must succeed before any candidate handshake; failure tears the child down. Clear after
    /// candidate disposal or terminalization through the same bundle that recorded it. The factory's
    /// unwired default throws on Record and ignores Clear; registration replaces both atomically.
    /// </summary>
    public AcpPidRecordCallbacks PidCallbacks { get; set; } = AcpPidRecordCallbacks.Unwired;

    //<summary>Delays BETWEEN the up-to-3 candidate spawns of one incident (t=0, +1s, +4s).</summary>
    public IReadOnlyList<TimeSpan> AttemptDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>Bounded wait for the old process tree's CONFIRMED exit during corpse retirement
    /// (unconfirmed exit is terminal for the incident, never shrugged past).</summary>
    public TimeSpan RetirementWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Bounded wait for the incident turn's settlement after commit. Generous —
    /// the faulted turn's awaits were already faulted, so completion is prompt; a pathological hang
    /// goes terminal rather than waiting forever.</summary>
    public TimeSpan SettlementWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Lifetime cap of COUNTED resumes per session (a
    /// resume counts exactly when the atomic reopen commits). The next crash past the cap
    /// finalizes — a child that dies after every resume is a broken installation, not a transient.</summary>
    public int MaxResumesPerSession { get; init; } = 5;
}

/// <summary>The atomic PID-record callback bundle — see
/// <see cref="AcpReconnectSupport.PidCallbacks"/> for the contract and why it is one reference,
/// never two setters.</summary>
internal sealed record AcpPidRecordCallbacks(Action<int> Record, Action Clear) {
    /// <summary>
    /// Recording before the orchestrator wires PID callbacks must fail closed; clearing has nothing to remove.
    /// </summary>
    public static readonly AcpPidRecordCallbacks Unwired = new(
        _ => throw new InvalidOperationException(
            "ACP reconnect: PID recorder not yet wired (crash raced launch registration)."),
        () => { });
}

/// <summary>
/// Thrown inside the reconnect owner when a condition is terminal for the whole INCIDENT (not just
/// the current attempt): a `session/load` JSON-RPC refusal (both measured refusal classes are
/// durable), a protocol downgrade, `loadSession` withdrawn, unconfirmed corpse retirement, or a
/// settlement-wait timeout. The owner stops attempting and finalizes.
/// </summary>
internal sealed class AcpReconnectTerminalException(string reason, string message) : Exception(message) {
    public string Reason { get; } = reason;
}
