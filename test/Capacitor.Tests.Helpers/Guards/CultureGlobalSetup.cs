using System.Globalization;

namespace Capacitor.Tests.Helpers.Guards;

/// <summary>
/// Assembly-wide pin of the default culture to the invariant one, so a formatted number or date reads
/// the same on every machine. A test that needs another culture sets <c>CultureInfo.CurrentCulture</c>
/// and restores it; that flows with its async context and does not reach other tests.
/// </summary>
public class CultureGlobalSetup {
    [BeforeEvery(Assembly)]
    public static void PinCulture() {
        CultureInfo.DefaultThreadCurrentCulture   = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
    }
}
