namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>What a recorder has sent from a VS Code Local session's chat log. A drain stages lines and the recorder
/// commits them only once their batch lands, so a failed send stages them again.</summary>
public sealed class CopilotLocalUsageState(VsCodeChatSessionCache log) {
    public VsCodeChatSessionCache Log { get; } = log;

    public HashSet<string> SentRequests { get; } = new(StringComparer.Ordinal);

    /// <summary>Each request's credits as last sent, in nano-AIU.</summary>
    public Dictionary<string, long> SentCredits { get; } = new(StringComparer.Ordinal);

    public List<string> StagedRequests { get; } = [];

    public List<(string RequestId, long NanoAiu)> StagedCredits { get; } = [];

    public void Unstage() {
        StagedRequests.Clear();
        StagedCredits.Clear();
    }

    /// <summary>Sends everything again on the next drain; each line's id comes from what it reports, so the server
    /// drops what it already holds.</summary>
    public void ForgetSent() {
        SentRequests.Clear();
        SentCredits.Clear();
        Unstage();
    }

    public void Commit() {
        SentRequests.UnionWith(StagedRequests);
        foreach (var (requestId, nano) in StagedCredits) {
            if (!SentCredits.TryGetValue(requestId, out var sent) || nano > sent) SentCredits[requestId] = nano;
        }
        Unstage();
    }
}
