using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.RepoEvidence;

/// <summary>The vendors whose transcripts show the PRs their agent creates. A vendor without an adapter links none.</summary>
public static class CreatedPullRequestAdapters {
    public static ICreatedPullRequestAdapter? For(string vendor) => vendor switch {
        "claude"  => ClaudeCreatedPullRequestAdapter.Instance,
        "codex"   => CodexCreatedPullRequestAdapter.Instance,
        "copilot" => CopilotCreatedPullRequestAdapter.Instance,
        "pi"      => PiCreatedPullRequestAdapter.Instance,
        _         => null,
    };
}
