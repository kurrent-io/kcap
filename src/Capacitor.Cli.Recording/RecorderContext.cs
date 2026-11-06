using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Recording;

/// <summary>What a recorder needs from the process hosting it.</summary>
/// <param name="Server">Where its spool and held-line files live, and which server they belong to.</param>
/// <param name="Vendor">Its housekeeping between drains.</param>
/// <param name="RedactionClock">The clock redaction budgets read.</param>
/// <param name="Log">Where it logs: a recorder never touches the console, because it may not own the process.</param>
/// <param name="Beat">Called at least once a loop, and before each long wait, so its host can tell a wedged
/// loop from a quiet one.</param>
public sealed record RecorderContext(CapacitorServer Server, IRecorderVendor Vendor, TimeProvider RedactionClock, Action<string> Log, Action Beat);
