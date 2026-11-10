using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>Copilot's per-session <c>workspace.yaml</c> carries its own <c>name</c>, auto-generated unless
/// <c>user_named</c> is set — the same file <c>CopilotImportSource</c> reads for import.</summary>
public sealed class CopilotWorkspaceTitle(string workspaceYamlPath) : IHarnessTitleStore {
    public bool RecordsChangeTime => false;

    public StoreTitle? Read() {
        var meta = CopilotWorkspaceYaml.TryRead(workspaceYamlPath);

        return string.IsNullOrWhiteSpace(meta?.Name)
            ? null
            : new StoreTitle(meta.Name.Trim(), meta.UserNamed ? HarnessTitleKind.Rename : HarnessTitleKind.Auto, null);
    }
}
