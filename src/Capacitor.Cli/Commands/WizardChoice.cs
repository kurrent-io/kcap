using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>A wizard picker whose answer stays on screen: Spectre's selection prompt erases itself once answered,
/// which would leave a scrolled-back wizard showing the question's context but not what was chosen.</summary>
static class WizardChoice {
    public static T Ask<T>(IAnsiConsole console, SelectionPrompt<T> prompt, Func<T, string> label) where T : notnull =>
        Ask(console, prompt, prompt.Title ?? "", label);

    /// <param name="title">The prompt's title, as markup.</param>
    internal static T Ask<T>(IAnsiConsole console, IPrompt<T> prompt, string title, Func<T, string> label) {
        var picked = console.Prompt(prompt);

        console.MarkupLine(Echo(title, label(picked)));

        return picked;
    }

    internal static string Echo(string title, string label) => $"  {title} {Markup.Escape(label)}";
}
