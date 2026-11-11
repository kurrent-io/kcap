using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core;

/// <summary>
/// Daemon configuration, service installation, and reviewer affirmation must share this registry.
/// An opt-out missing from service environment forwarding cannot disable its reviewer through the
/// supported install path. Every gated reviewer therefore needs a non-null enable variable.
/// </summary>
public sealed record GatedReviewer(
        string Vendor, string DefaultBinary, string PathEnvVar, string EnableEnvVar);

/// <summary>The registry. Adding an entry here is what wires a new gated reviewer everywhere.</summary>
public static class GatedReviewers {
    /// <summary>
    /// Every reviewer with an opt-out switch and an affirmable floor.
    ///
    /// <para>Order is the order an operator sees in <c>--vendor</c> usage text; nothing depends on
    /// it.</para>
    /// </summary>
    public static readonly GatedReviewer[] All = [
        For<KiroHarness>       ("KCAP_KIRO_UNATTENDED_REVIEWER"),
        For<GeminiHarness>     ("KCAP_GEMINI_UNATTENDED_REVIEWER"),
        For<AntigravityHarness>("KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER"),
        For<OpenCodeHarness>   ("KCAP_OPENCODE_UNATTENDED_REVIEWER"),
        For<PiHarness>         ("KCAP_PI_UNATTENDED_REVIEWER"),
        For<GrokHarness>       ("KCAP_GROK_UNATTENDED_REVIEWER"),
        For<MuseHarness>       ("KCAP_MUSE_UNATTENDED_REVIEWER")
    ];

    /// <summary>The opt-out switch is the only thing a row states for itself. The vendor token and its
    /// binary are the harness's; the path override is <see cref="HarnessOverrides"/>'s.</summary>
    static GatedReviewer For<TSelf>(string enableEnvVar) where TSelf : IHarness<TSelf> =>
        new(TSelf.Id.VendorId, TSelf.CliBinary, TSelf.Id.PathEnvVar, enableEnvVar);

    /// <summary>For usage text: <c>kiro | gemini | antigravity | opencode | pi | grok | muse</c>.</summary>
    public static string VendorList => string.Join(" | ", All.Select(r => r.Vendor));

    /// <summary>Case-insensitive lookup, null when the vendor is not gated (or not a vendor).</summary>
    public static GatedReviewer? Resolve(string? vendor) =>
        All.FirstOrDefault(r => string.Equals(r.Vendor, vendor, StringComparison.OrdinalIgnoreCase));
}
