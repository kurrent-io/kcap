using Capacitor.Client;

namespace Capacitor.Cli.Ide;

/// <summary>One client subscription: the project it selects, and the rows the client holds for it as they were last sent.</summary>
internal sealed class IdeSubscription(string id, ProjectScope scope) {
    public string Id { get; } = id;

    public ProjectScope Scope { get; } = scope;

    public Dictionary<string, SessionRow> Sent { get; } = new(StringComparer.Ordinal);
}
