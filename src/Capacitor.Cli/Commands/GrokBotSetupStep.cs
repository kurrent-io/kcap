namespace Capacitor.Cli.Commands;

/// <summary>Setup's one question about the Grok Bot desktop app: whether the daemon should record its
/// chats. Asked once per profile; the answer either way is kept, so a rerun does not ask again.</summary>
internal static class GrokBotSetupStep {
    internal const string Hint = "  Found the Grok Bot desktop app — record its chats with: kcap config set grok_bot.record true";

    /// <param name="recorded">The profile's stored answer, null when it was never asked.</param>
    /// <returns>The answer to store, or null to leave the profile as it is.</returns>
    internal static bool? Run(bool detected, bool? recorded, bool noPrompt, bool interactive, Func<string, bool> promptYesNo, Action<string> writeLine) {
        if (!detected) return null;

        if (recorded is { } answered) {
            if (answered) writeLine("  [green]✓[/] Grok Bot chats are recorded by the daemon");

            return null;
        }

        if (noPrompt || !interactive) {
            writeLine(Hint);

            return null;
        }

        writeLine("  Found the Grok Bot desktop app. Recording covers every chat it holds on this computer, earlier ones included:");
        writeLine("  your messages, the Bot's messages and the answers you chose, as sessions private to you.");

        try {
            return promptYesNo("Record your Grok Bot chats too?");
        } catch (InvalidOperationException) {
            writeLine(Hint);

            return null;
        }
    }
}
