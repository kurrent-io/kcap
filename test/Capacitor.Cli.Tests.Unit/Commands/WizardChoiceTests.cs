using Capacitor.Cli.Commands;
using Spectre.Console;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Spectre's selection prompt erases itself once answered, so the wizard echoes the answer under its title.</summary>
public class WizardChoiceTests {
    sealed class Answer<T>(T value) : IPrompt<T> {
        public T Show(IAnsiConsole console) => value;
        public Task<T> ShowAsync(IAnsiConsole console, CancellationToken cancellationToken) => Task.FromResult(value);
    }

    [Test]
    public async Task The_answer_is_echoed_under_its_title() {
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(output),
        });

        var picked = WizardChoice.Ask(console, new Answer<string>("Keyword search only"), "Search by meaning with:", s => s);

        await Assert.That(picked).IsEqualTo("Keyword search only");
        await Assert.That(output.ToString().TrimEnd()).IsEqualTo("  Search by meaning with: Keyword search only");
    }

    [Test]
    public async Task A_label_is_echoed_as_text_not_markup() =>
        await Assert.That(WizardChoice.Echo("Which provider?", "Ollama [local]")).IsEqualTo("  Which provider? Ollama [[local]]");
}
