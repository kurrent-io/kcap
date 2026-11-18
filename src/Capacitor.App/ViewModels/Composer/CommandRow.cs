using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels.Composer;

public sealed record CommandRow(HostedAgentCommand Command, char Sigil) {
    public string Text => $"{Sigil}{Command.Name}";
}
