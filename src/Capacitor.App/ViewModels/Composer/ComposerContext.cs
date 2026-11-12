using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.ViewModels.Composer;

public sealed record ComposerContext(string Directory, HarnessId? Harness, string? AgentId = null);
