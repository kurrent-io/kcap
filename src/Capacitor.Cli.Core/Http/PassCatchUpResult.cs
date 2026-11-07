namespace Capacitor.Cli.Core.Http;

public abstract record PassCatchUpResult {
    public sealed record Answered(CatchUpDto CatchUp) : PassCatchUpResult;

    /// <summary>The server offers passes but has no catch-up route.</summary>
    public sealed record NotOffered : PassCatchUpResult;
}
