namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>What a recorder has sent from a VS Code Local session's chat log. A drain stages lines and the recorder
/// commits them only once their batch lands, so a failed send stages them again.</summary>
public sealed class CopilotLocalUsageState(VsCodeChatSessionCache log) {
    public VsCodeChatSessionCache Log { get; } = log;

    public HashSet<string> SentRequests { get; } = new(StringComparer.Ordinal);

    public long SentNanoAiu { get; private set; }

    public List<string> StagedRequests { get; } = [];

    public long? StagedNanoAiu { get; set; }

    public void Unstage() {
        StagedRequests.Clear();
        StagedNanoAiu = null;
    }

    public void Commit() {
        SentRequests.UnionWith(StagedRequests);
        if (StagedNanoAiu is { } nano && nano > SentNanoAiu) SentNanoAiu = nano;
        Unstage();
    }
}
