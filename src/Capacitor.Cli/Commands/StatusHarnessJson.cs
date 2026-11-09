namespace Capacitor.Cli.Commands;

/// <param name="InstallCommand">What to run to wire this one up, when it is installed and unwired.</param>
/// <param name="LastHookEvent">When this agent last ran a kcap hook on this machine; null when wired
/// but not yet run, and always null when unwired.</param>
/// <param name="HooksTrust">Codex only: <c>trusted</c> or <c>untrusted</c> as Codex answered for kcap's installed
/// hooks, null when there was no answer. Untrusted hooks read as not wired.</param>
public sealed record StatusHarnessJson(
    string Vendor, bool Installed, bool Wired, string? InstallCommand, DateTimeOffset? LastHookEvent = null,
    string? HooksTrust = null);
