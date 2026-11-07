using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary><c>kcap message &lt;who&gt; &lt;text&gt; [--session|--work-item|--page|--agent &lt;id&gt;] [--reply-to
/// &lt;pass-id&gt;]</c>. Left out of help, and answers as an unknown command does while the server does not offer
/// passes.</summary>
sealed class MessageCommand(IPassesApi passes, string serverUrl) {
    internal const string Usage =
        "Usage: kcap message <who> <text> [--session|--work-item|--page|--agent <id>] [--reply-to <pass-id>]";

    public Task<int> HandleAsync(string[] args) => HandleAsync(args, PassTerminal.FromConsole());

    internal async Task<int> HandleAsync(string[] args, PassTerminal terminal) {
        if (await PassSending.HiddenAsync(passes, "message", terminal) is { } hidden) return hidden;

        string? who = null, text = null, itemKind = null, itemId = null, replyTo = null;

        for (var i = 1; i < args.Length; i++) {
            var arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal)) {
                if (who is null) who = arg;
                else if (text is null) text = arg;
                else return await UsageAsync(terminal);
                continue;
            }

            if (i + 1 >= args.Length) return await UsageAsync(terminal);
            var value = args[++i];

            switch (arg) {
                case "--session" or "--work-item" or "--page" or "--agent" when itemKind is null:
                    itemKind = PassItemKinds.FromFlag(arg);
                    itemId   = value;
                    break;
                case "--reply-to":
                    replyTo = value;
                    break;
                default:
                    return await UsageAsync(terminal);
            }
        }

        if (who is null || string.IsNullOrWhiteSpace(text)) return await UsageAsync(terminal);

        if (await PassSending.RecipientAsync(passes, who, terminal) is not { } recipient) return 1;

        var body = new StartPassBody("message", recipient.Id, itemKind, itemId, Note: text, ReplyTo: replyTo);

        return await PassSending.SendAsync(passes, serverUrl, body, recipient.DisplayName ?? recipient.Id, terminal);
    }

    static async Task<int> UsageAsync(PassTerminal terminal) {
        await terminal.Error.WriteLineAsync(Usage);

        return 1;
    }
}
