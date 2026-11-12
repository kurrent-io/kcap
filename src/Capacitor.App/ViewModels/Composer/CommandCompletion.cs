using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.ViewModels.Composer;

/// "/" opens the list for every harness, and so does the harness's own sigil; a pick goes in under that sigil.
public sealed class CommandCompletion(
        Func<ComposerContext, Task<IReadOnlyList<HostedAgentCommand>>> list,
        Func<HarnessId?, char> sigilOf) {
    Task<IReadOnlyList<HostedAgentCommand>>? _listed;

    /// Null when the text is not a command query.
    public async Task<IReadOnlyList<object>?> RowsAsync(string text, ComposerContext context) {
        var sigil = sigilOf(context.Harness);
        if (text is not [var opener, .. var query] || opener != '/' && opener != sigil || query.Any(char.IsWhiteSpace)) {
            Reset();
            return null;
        }

        return [.. Rank(await (_listed ??= list(context)), query).Select(command => new CommandRow(command, sigil))];
    }

    public void Reset() => _listed = null;

    public void Prefetch(ComposerContext context) => _ = list(context);

    public static (string Text, int Caret) Insert(CommandRow row) {
        var line = $"{row.Text} ";
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
