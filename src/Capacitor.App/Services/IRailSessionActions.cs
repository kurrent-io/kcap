namespace Capacitor.App.Services;

public interface IRailSessionActions {
    void Rename(AgentRow row);
    Task MakePrivateAsync(AgentRow row, string label);
    /// Asks before it deletes: the session's server data cannot be brought back.
    Task DeleteAsync(AgentRow row, string label);
    void Terminate(AgentRow row, string label);
}
