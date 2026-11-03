using System.Text.Json;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Commands;

/// <summary>Pure renderer for the list payload — kept separate from I/O so it's directly testable.</summary>
internal static class HarnessListRender {
    public static string Render(HarnessRegistry harnesses, HarnessOfferLedger ledger, CodexHooksTrust codexTrust = CodexHooksTrust.Unknown) {
        var entries = new List<HarnessListEntryJson>();

        foreach (var harness in harnesses) {
            var agent = harnesses.Detect(harness.Id);
            var wired = harness.Signals.IsWired;
            entries.Add(new HarnessListEntryJson(
                harness.VendorId,
                harness.Label,
                agent.BinaryFound,
                agent.InstallSignalFound,
                wired,
                agent.DesktopAppFound,
                ledger.Entry(harness.Id) is { Declined: true },
                harness.Id == HarnessId.Codex && wired ? StatusCommand.HooksTrustToken(codexTrust) : null));
        }

        return JsonSerializer.Serialize(new HarnessListJson(entries), HarnessListJsonContext.Default.HarnessListJson);
    }

    /// <summary>The text table's "kcap wired" cell: Codex hooks it has not trusted record nothing, so they never read as wired.</summary>
    public static string WiredCell(HarnessId id, bool wired, CodexHooksTrust codexTrust) =>
        !wired ? "no"
        : id == HarnessId.Codex && codexTrust == CodexHooksTrust.Untrusted ? "not trusted"
        : "yes";
}
