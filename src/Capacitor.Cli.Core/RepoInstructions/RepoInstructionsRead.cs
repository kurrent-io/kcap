namespace Capacitor.Cli.Core.RepoInstructions;

/// <summary>A checkout's instruction files as its default branch holds them, or why they could not be read.</summary>
public sealed record RepoInstructionsRead(RepoInstructionSet? Set, string? Unavailable) {
    public static RepoInstructionsRead Of(RepoInstructionSet set) => new(set, null);

    public static RepoInstructionsRead Not(string reason) => new(null, reason);
}
