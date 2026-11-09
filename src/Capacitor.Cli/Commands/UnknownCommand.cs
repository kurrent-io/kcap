namespace Capacitor.Cli.Commands;

/// <summary>The one answer to a command kcap does not offer, shared by commands that hide themselves when the server
/// does not offer them.</summary>
static class UnknownCommand {
    public static int Write(TextWriter error, string command) {
        error.WriteLine($"Unknown command: {command}");

        return 1;
    }
}
