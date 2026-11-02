namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>What the server's last session-vector pass did. <see cref="NotRun"/> is kept in memory, so it is what
/// every server reports between a start and its first pass, whatever vectors it stored before.</summary>
public enum VectorStorageState {
    /// <summary>A state this build does not know.</summary>
    Unknown,
    NotRun,
    Ok,

    /// <summary>The last pass failed and stored nothing: search falls back to keywords for the sessions it missed.</summary>
    Failing,
}

public static class VectorStorageStateExtensions {
    public static VectorStorageState FromWire(string? wire) => wire switch {
        "not_run" => VectorStorageState.NotRun,
        "ok"      => VectorStorageState.Ok,
        "failing" => VectorStorageState.Failing,
        _         => VectorStorageState.Unknown,
    };

    public static string Wire(this VectorStorageState state) => state switch {
        VectorStorageState.NotRun  => "not_run",
        VectorStorageState.Ok      => "ok",
        VectorStorageState.Failing => "failing",
        _                          => "unknown",
    };
}
