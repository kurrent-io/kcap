namespace Capacitor.Cli.Harness.Claude;

/// <summary>What Claude Code exports into every hook it runs as <c>CLAUDE_CODE_ENTRYPOINT</c>.</summary>
static class ClaudeEntrypoint {
    public const string Var = "CLAUDE_CODE_ENTRYPOINT";

    /// <summary><c>claude -p</c>. An interactive session reports <c>cli</c>, and an SDK host
    /// (<c>sdk-ts</c>, <c>sdk-py</c>) drives a conversation of its own, so neither counts.</summary>
    public static bool IsHeadless(Func<string, string?> env) => env(Var) is "sdk-cli";
}
