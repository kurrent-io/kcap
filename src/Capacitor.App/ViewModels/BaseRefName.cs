namespace Capacitor.App.ViewModels;

/// The launcher's check on a typed base ref, so an obvious typo never leaves the app. It follows
/// git check-ref-format's rules for a branch name; the daemon still runs git's own check and
/// decides whether the ref exists.
public static class BaseRefName {
    const int MaxLength = 255;

    /// Null when the name is acceptable, otherwise the reason it is not.
    public static string? Problem(string name) {
        if (name.Length == 0) return "Enter a branch, tag or commit.";
        if (name.Length > MaxLength) return "That name is too long.";
        if (name == "@") return "“@” is not a branch name.";
        if (name[0] == '-') return "A branch name can't start with “-”.";
        if (name[0] == '/' || name[^1] == '/' || name.Contains("//", StringComparison.Ordinal))
            return "A branch name can't start or end with “/”, or contain “//”.";
        if (name[^1] == '.') return "A branch name can't end with “.”.";
        if (name.Contains("..", StringComparison.Ordinal)) return "A branch name can't contain “..”.";
        if (name.Contains("@{", StringComparison.Ordinal)) return "A branch name can't contain “@{”.";

        foreach (var c in name)
            if (c < 0x20 || c == 0x7f || c == ' ')
                return "A branch name can't contain spaces.";
            else if (c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                return $"A branch name can't contain “{c}”.";

        foreach (var part in name.Split('/'))
            if (part.StartsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal))
                return "A part of a branch name can't start with “.” or end with “.lock”.";

        return null;
    }
}
