using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>Pi's <c>get_commands</c> RPC: its prompt templates, skills (as <c>skill:name</c>) and extension
/// commands, each run as <c>/name</c>. Pi's own TUI commands are not listed.</summary>
public static class PiCommands {
    public const string Command = "get_commands";

    public static IReadOnlyList<HostedAgentCommand> Extract(JsonElement response) {
        if (response.Bool("success") != true || response.Obj("data")?.Arr("commands") is not { } commands) return [];

        var list = new List<HostedAgentCommand>();
        foreach (var entry in commands.EnumerateArray()) {
            if (!entry.IsObject || entry.Str("name") is not { Length: > 0 } name) continue;

            var description = entry.Str("description");
            list.Add(new HostedAgentCommand(name, string.IsNullOrWhiteSpace(description) ? null : description, ArgumentHint: null));
        }

        return list;
    }
}
