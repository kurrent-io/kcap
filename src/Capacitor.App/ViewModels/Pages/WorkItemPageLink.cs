using Lucide.Avalonia;

namespace Capacitor.App.ViewModels.Pages;

/// A PR or issue row: its key, the tracker's title and state.
public sealed record WorkItemPageLink(string ShortKey, string? Title, string? State, string? Url, bool IsPr) {
    public bool IsMerged => string.Equals(State, "merged", StringComparison.OrdinalIgnoreCase);
    public LucideIconKind Icon => !IsPr ? LucideIconKind.CircleDot : IsMerged ? LucideIconKind.GitMerge : LucideIconKind.GitPullRequest;
    public string Meta => $"{(IsPr ? "Pull request" : "Issue")} · {StateLabel}";
    public string StateLabel => State is { Length: > 0 } state ? char.ToUpperInvariant(state[0]) + state[1..].Replace('_', ' ') : "Open";
}
