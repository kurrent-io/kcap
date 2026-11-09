using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// The find each session's chat had open, held for the app's lifetime: a workspace is torn down
/// on every navigation away, so a search left open has to outlive its chat tab.
public sealed class ChatFindMemory {
    readonly Dictionary<string, ChatFindState> _states = new(StringComparer.Ordinal);

    // The origin is part of the key: a local and a remote agent can share an id and are different agents.
    static string Key(AgentOrigin origin, string agentId) => $"{origin}:{AgentIds.Normalize(agentId) ?? agentId}";

    public ChatFindState? Get(AgentOrigin origin, string agentId) => _states.GetValueOrDefault(Key(origin, agentId));

    public void Set(AgentOrigin origin, string agentId, ChatFindState? state) {
        if (state is null) _states.Remove(Key(origin, agentId));
        else _states[Key(origin, agentId)] = state;
    }
}
