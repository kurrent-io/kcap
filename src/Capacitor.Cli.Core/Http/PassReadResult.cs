namespace Capacitor.Cli.Core.Http;

public abstract record PassReadResult {
    public sealed record Found(PassDto Pass) : PassReadResult;

    /// <summary>No such pass, or not one the caller sent or received.</summary>
    public sealed record NotFound : PassReadResult;
}
