using Capacitor.Cli.Core.Setup.Intelligence;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>The terminal's <see cref="IIntelligenceQuestions"/>.</summary>
sealed class IntelligenceQuestions : IIntelligenceQuestions {
    public const string Explanation =
        "kcap titles your sessions, says what each achieved and runs evals in the background. Something has to run "
      + "those model calls: your own coding agent on this machine, on its sign-in and quota, or a provider with your API key.";

    /// <summary>Only the harnesses that run headless prompts can serve here; Copilot and Cursor cannot.</summary>
    public const string ProviderOnlyExplanation =
        "kcap titles your sessions, says what each achieved and runs evals in the background. Something has to run "
      + "those model calls: a provider with your API key. Of coding agents, only Claude Code, Codex, Pi and OpenCode can run them.";

    public string Choose(IReadOnlyList<string> harnesses) {
        AnsiConsole.MarkupLine($"  {Markup.Escape(harnesses.Count > 0 ? Explanation : ProviderOnlyExplanation)}");

        var choices = harnesses.Select(h => (Token: h, Label: $"My local agent: {HeadlessHarnessProbe.Label(h)}"))
            .Append((Token: LocalIntelligenceStep.ProviderChoice, Label: "A provider, with my API key"))
            .Append((Token: IntelligenceAnswer.NoneToken, Label: "Not now"))
            .ToList();

        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Who runs background intelligence?")
            .AddChoices(choices.Select(c => c.Label)));

        return choices.First(c => c.Label == picked).Token;
    }

    public IntelligenceVendor Vendor() {
        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Which provider?").AddChoices(IntelligenceVendor.All.Select(v => v.Label)));

        return IntelligenceVendor.All.First(v => v.Label == picked);
    }

    public string Model(IntelligenceVendor vendor) =>
        AnsiConsole.Prompt(new TextPrompt<string>("Model:").DefaultValue(vendor.DefaultModel));

    public string? Key(IntelligenceVendor vendor) => LocalIntelligenceCommand.AskKey(vendor);

    public bool ImportOutcomes() =>
        AnsiConsole.Prompt(new ConfirmationPrompt("Also summarize what each imported session achieved?") { DefaultValue = true });
}
