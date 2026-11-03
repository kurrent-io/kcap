using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Tests.Unit;

public class WebKitNavigationGuardTests {
    [Test]
    public async Task Without_a_web_view_nothing_is_installed() {
        await Assert.That(WebKitNavigationGuard.TryInstall(0)).IsFalse();
    }
}
