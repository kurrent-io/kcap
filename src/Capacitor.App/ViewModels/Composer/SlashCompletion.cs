using System.Text.RegularExpressions;
using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels.Composer;

public sealed partial class SlashCompletion(Func<ComposerContext, Task<IReadOnlyList<HostedAgentCommand>>> list) {
    Task<IReadOnlyList<HostedAgentCommand>>? _listed;

    [GeneratedRegex(@"^/(\S*)$")]
    private static partial Regex Query();

    /// Null when the text is not a slash query.
    public async Task<IReadOnlyList<object>?> RowsAsync(string text, ComposerContext context) {
        if (Query().Match(text) is not { Success: true } query) {
            Reset();
            return null;
        }

        return [.. Rank(await (_listed ??= list(context)), query.Groups[1].Value)];
    }

    public void Reset() => _listed = null;

    public void Prefetch(ComposerContext context) => _ = list(context);

    public static (string Text, int Caret) Insert(HostedAgentCommand command) {
        var line = $"/{command.Name} ";
        return (line, line.Length);
    }

    internal static IEnumerable<HostedAgentCommand> Rank(IReadOnlyList<HostedAgentCommand> commands, string query) {
        return commands.Select(command => (Command: command, Score: Score(command)))
            .Where(m => m.Score >= 0)
            .OrderBy(m => m.Score)
            .ThenBy(m => m.Command.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.Command);

        int Score(HostedAgentCommand command) {
            var name = command.Name;
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.Contains($":{query}", StringComparison.OrdinalIgnoreCase) || name.Contains($"-{query}", StringComparison.OrdinalIgnoreCase)) return 1;
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;

            return command.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ? 3 : -1;
        }
    }
}
