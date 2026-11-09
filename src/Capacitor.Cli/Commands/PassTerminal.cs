namespace Capacitor.Cli.Commands;

/// <param name="Interactive">Whether someone is at a terminal to answer a question; without one, nothing is approved.</param>
sealed record PassTerminal(TextWriter Out, TextWriter Error, bool Interactive, Func<string?> ReadLine) {
    public static PassTerminal FromConsole() => new(Console.Out, Console.Error, !Console.IsInputRedirected, Console.ReadLine);
}
