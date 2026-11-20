using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.ViewModels.Composer;

/// Account is the one a launch would use; null runs under the vendor's default home.
public sealed record ComposerContext(string Directory, HarnessId? Harness, string? AgentId = null, ResolvedAccount? Account = null) {
    public static ResolvedAccount? AccountOf(IReadOnlyList<AccountChoice> rows) =>
        rows.FirstOrDefault(c => c is { Selected: true, Removed: false })?.Row is { } row
            ? new ResolvedAccount(row.Directory, row.Id, row.Label)
            : null;
}
