using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.ViewModels.Composer;

/// Where a composer's prompt runs: the checkout its @ paths resolve against, the harness whose
/// commands / lists, and the agent already running there, if any.
public sealed record ComposerContext(string Directory, HarnessId? Harness, string? AgentId = null);
