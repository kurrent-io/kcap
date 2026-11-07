using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap pass &lt;who&gt; (--session|--work-item|--page|--agent) &lt;id&gt; [--kind look|take-over|pair]
/// [--brief &lt;text&gt;] [--prompt &lt;text&gt;]... [--no-reminder]</c>. Left out of help, and answers as an unknown
/// command does while the server does not offer passes.</summary>
sealed class PassCommand(IPassesApi passes, string serverUrl) {
    internal const string Usage =
        "Usage: kcap pass <who> (--session|--work-item|--page|--agent) <id> [--kind look|take-over|pair] [--brief <text>] [--prompt <text>]... [--no-reminder]";

    public Task<int> HandleAsync(string[] args) => HandleAsync(args, PassTerminal.FromConsole());

    internal async Task<int> HandleAsync(string[] args, PassTerminal terminal) {
        if (await PassSending.HiddenAsync(passes, "pass", terminal) is { } hidden) return hidden;

        string? who = null, itemKind = null, itemId = null, brief = null;
        var kind     = "look";
        var prompts  = new List<string>();
        var reminder = true;

        for (var i = 1; i < args.Length; i++) {
            var arg = args[i];

            if (arg == "--no-reminder") {
                reminder = false;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal)) {
                if (who is not null) return await UsageAsync(terminal);
                who = arg;
                continue;
            }

            if (i + 1 >= args.Length) return await UsageAsync(terminal);
            var value = args[++i];

            switch (arg) {
                case "--session" or "--work-item" or "--page" or "--agent" when itemKind is null:
                    itemKind = PassItemKinds.FromFlag(arg);
                    itemId   = value;
                    break;
                case "--kind":
                    kind = value.Replace('-', '_');
                    break;
                case "--brief":
                    brief = value;
                    break;
                case "--prompt":
                    prompts.Add(value);
                    break;
                default:
                    return await UsageAsync(terminal);
            }
        }

        if (who is null || itemKind is null) return await UsageAsync(terminal);

        if (await PassSending.RecipientAsync(passes, who, terminal) is not { } recipient) return 1;

        var body = new StartPassBody(kind, recipient.Id, itemKind, itemId, Brief: brief, SuggestedPrompts: prompts.Count > 0 ? prompts : null,
                                     ReminderOff: reminder ? null : true);

        return await PassSending.SendAsync(passes, serverUrl, body, recipient.DisplayName ?? recipient.Id, terminal);
    }

    static async Task<int> UsageAsync(PassTerminal terminal) {
        await terminal.Error.WriteLineAsync(Usage);

        return 1;
    }
}
