using Capacitor.Models.Transcripts.Harness.MistralVibe;

namespace Capacitor.Cli.Recording.Harness.MistralVibe;

/// <summary>A child session a <c>subagent.spawn</c> effect started, named by its store directory
/// (<c>child-&lt;hex&gt;</c>, a sibling of the parent's).</summary>
public sealed record MistralVibeSubagent(string ChildSessionId, string? AgentType) {
    /// <summary>The id the child is recorded under, the one its spawning call names; null for a name
    /// that is not a child store.</summary>
    public string? AgentId => MistralVibeExtension.AgentIdOf(ChildSessionId);
}
