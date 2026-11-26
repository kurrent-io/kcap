namespace Capacitor.Cli.Core;

/// <summary>What a registered daemon can do, sent on its own when it changes. Every field carries its whole current
/// value with the meaning it has on <see cref="DaemonConnect"/>, so the server replaces its copy of each and leaves the
/// agents, accounts and repo paths a connect carries alone.</summary>
public sealed record DaemonCapabilitiesUpdate(
    int                                        MaxAgents,
    string[]?                                  SupportedVendors,
    string[]?                                  UnattendedVendors,
    IReadOnlyList<UnattendedVendorCapability>? UnattendedVendorCapabilities,
    string[]?                                  AcpPresetVendors,
    string[]?                                  PermissionModeVendors,
    string[]?                                  PrReviewVendors,
    Dictionary<string, VendorModelOption[]>?   VendorModels,
    Dictionary<string, VendorModelOption[]>?   VendorModelsByAccount,
    string[]?                                  HeadlessVendors,
    string[]?                                  Capabilities
);
