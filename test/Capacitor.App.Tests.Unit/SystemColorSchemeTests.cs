using Avalonia.Platform;
using NSubstitute;

namespace Capacitor.App.Tests.Unit;

/// Auto must open in the desktop's real preference on Linux, where the platform answers Light until its
/// own read of that preference lands.
public class SystemColorSchemeTests {
    static IPlatformSettings Platform(PlatformThemeVariant variant) {
        var platform = Substitute.For<IPlatformSettings>();
        platform.GetColorValues().Returns(new PlatformColorValues { ThemeVariant = variant });
        return platform;
    }

    static void Report(IPlatformSettings platform, PlatformThemeVariant variant) {
        var values = new PlatformColorValues { ThemeVariant = variant };
        platform.GetColorValues().Returns(values);
        platform.ColorValuesChanged += Raise.Event<EventHandler<PlatformColorValues>>(platform, values);
    }

    [Test]
    public async Task The_startup_preference_stands_until_the_platform_reports() {
        var platform = Platform(PlatformThemeVariant.Light);
        using var system = new SystemColorScheme(platform, startupPrefersDark: true);
        await Assert.That(system.IsDark).IsTrue();

        Report(platform, PlatformThemeVariant.Dark);
        await Assert.That(system.IsDark).IsTrue();
        Report(platform, PlatformThemeVariant.Light);
        await Assert.That(system.IsDark).IsFalse();
    }

    [Test]
    [Arguments(PlatformThemeVariant.Dark, true)]
    [Arguments(PlatformThemeVariant.Light, false)]
    public async Task Without_a_startup_preference_the_platform_answers(PlatformThemeVariant variant, bool dark) {
        using var system = new SystemColorScheme(Platform(variant), startupPrefersDark: null);
        await Assert.That(system.IsDark).IsEqualTo(dark);
    }
}
