using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.RepoInstructions;

/// <summary>The files a root instruction file pulls in with <c>@path</c>, as repo-relative paths. A home (<c>@~/</c>),
/// absolute or out-of-repo path is dropped, and so is anything inside a code fence.</summary>
public static partial class RepoInstructionImports {
    public static IReadOnlyList<string> Of(string content) {
        var    found = new List<string>();
        var    seen  = new HashSet<string>(StringComparer.Ordinal);
        char?  fence = null;
        var    fenceLength = 0;

        foreach (var line in content.Split('\n')) {
            if (FenceMarker(line) is var (marker, length)) {
                if (fence is null) (fence, fenceLength) = (marker, length);
                else if (marker == fence && length >= fenceLength && line.TrimStart(' ')[length..].Trim(' ', '\t', '\r').Length == 0) fence = null;
                continue;
            }
            if (fence is not null) continue;

            foreach (Match match in Import().Matches(line))
                if (Normalize(match.Groups[1].Value) is { } path && seen.Add(path)) found.Add(path);
        }

        return found;
    }

    /// <summary>The path relative to the repo root, forward slashes and no <c>.</c> or <c>..</c> segments, or null when it
    /// leaves the repo or is not relative.</summary>
    internal static string? Normalize(string target) {
        if (target.Length == 0 || target[0] is '~' or '/' || target.Contains('\\') || (target.Length > 1 && target[1] == ':')) return null;

        var segments = new List<string>();
        foreach (var segment in target.Split('/')) {
            switch (segment) {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count == 0) return null;
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(segment);
                    continue;
            }
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    static (char Marker, int Length)? FenceMarker(string line) {
        var trimmed = line.TrimStart(' ');
        if (line.Length - trimmed.Length > 3 || trimmed.Length < 3 || trimmed[0] is not ('`' or '~')) return null;

        var length = 0;
        while (length < trimmed.Length && trimmed[length] == trimmed[0]) length++;

        return length >= 3 ? (trimmed[0], length) : null;
    }

    [GeneratedRegex(@"(?<!\S)@(\S+)")]
    private static partial Regex Import();
}
