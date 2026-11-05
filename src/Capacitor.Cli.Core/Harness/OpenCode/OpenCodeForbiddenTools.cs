using System.Collections.Immutable;

namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// OpenCode tools an unattended run must never be allowed. Each is denied by name on top of <c>"*": "deny"</c>:
/// OpenCode resolves a specific key before the wildcard, so a file config's <c>bash: "allow"</c> would otherwise
/// survive the wildcard.
/// </summary>
/// <remarks>
/// <c>skill</c> is here because OpenCode reads skills from <c>~/.agents/skills</c> and <c>~/.claude</c>, which a
/// private config directory does not suppress; with the tool denied, OpenCode leaves the skills section out of
/// its system prompt.
/// </remarks>
public static class OpenCodeForbiddenTools {
    public static readonly ImmutableArray<string> Names =
        ["write", "edit", "patch", "bash", "webfetch", "websearch", "task", "skill", "todowrite"];
}
