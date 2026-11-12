using System.Text.RegularExpressions;

namespace Capacitor.App.ViewModels.Composer;

/// Each listing is read once per query and dropped when it ends, so the next one sees files created since.
public sealed partial class MentionCompletion(
        Func<string, Task<IReadOnlyList<string>>> repoFiles,
        Func<string, string, Task<IReadOnlyList<string>>> folderEntries) {
    const int MaxRows = 50;

    readonly Dictionary<string, Task<IReadOnlyList<string>>> _folders = new(StringComparer.Ordinal);
    Task<IReadOnlyList<string>>? _repo;

    [GeneratedRegex(@"(?:^|\s)@(\S*)$")]
    private static partial Regex Query();

    /// Null when no "@query" ends at the caret.
    public async Task<IReadOnlyList<object>?> RowsAsync(string text, int caret, ComposerContext context) {
        if (Query().Match(text[..caret]) is not { Success: true } match) {
            _repo = null;
            _folders.Clear();
            return null;
        }

        var query = match.Groups[1].Value;
        if (query is ['/', ..] or ['.', '/', ..] or ['.', '.', '/', ..] or ['~', '/', ..]) {
            var cut = query.LastIndexOf('/') + 1;
            var folder = query[..cut];
            if (!_folders.TryGetValue(folder, out var names)) _folders[folder] = names = folderEntries(context.Directory, folder);

            return [.. RankFolder(await names, folder, query[cut..])];
        }

        return [.. RankRepo(await (_repo ??= repoFiles(context.Directory)), query)];
    }

    /// A folder goes in without a trailing space, so the query carries on into it.
    public static (string Text, int Caret) Insert(FileMention mention, string text, int caret) {
        var start = caret - Query().Match(text[..caret]).Groups[1].Length - 1;
        var path = mention.Path;
        var inserted = path.EndsWith('/') ? $"@{path}" : path.Any(char.IsWhiteSpace) ? $"@\"{path}\" " : $"@{path} ";

        return (text[..start] + inserted + text[caret..], start + inserted.Length);
    }

    internal static IEnumerable<FileMention> RankFolder(IReadOnlyList<string> names, string folder, string part) =>
        names.Where(name => name.Contains(part, StringComparison.OrdinalIgnoreCase) && (part.StartsWith('.') || !name.StartsWith('.')))
            .OrderBy(name => !name.StartsWith(part, StringComparison.OrdinalIgnoreCase))
            .Take(MaxRows)
            .Select(name => new FileMention(folder + name));

    internal static IEnumerable<FileMention> RankRepo(IReadOnlyList<string> paths, string query) {
        return paths.Select(path => (Path: path, Score: Score(path)))
            .Where(m => m.Score >= 0)
            .OrderBy(m => m.Score)
            .ThenBy(m => m.Path.Length)
            .Take(MaxRows)
            .Select(m => new FileMention(m.Path));

        int Score(string path) {
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 1;

            return path.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : -1;
        }
    }
}
