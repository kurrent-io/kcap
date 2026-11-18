using System.Diagnostics;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit;

public class ChildProfileTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>A child of a process that resolved a profile gets that profile by name and no URL, which would
    /// outrank it and send the child to the active profile's token.</summary>
    [Test]
    public async Task A_resolved_profile_travels_by_name_without_a_url() {
        var psi = new ProcessStartInfo { Environment = { [ProfileOverrides.UrlVar] = "https://ambient.test" } };

        ChildProfile.Apply(psi, Resolutions.Of(new Profile { ServerUrl = "https://localhost:5101" }, "local"));

        await Assert.That(psi.Environment[ProfileOverrides.ProfileVar]).IsEqualTo("local");
        await Assert.That(psi.Environment.ContainsKey(ProfileOverrides.UrlVar)).IsFalse();
    }

    /// <summary>A process whose URL came from an override resolved no profile, so its child gets the URL.</summary>
    [Test]
    public async Task An_override_url_travels_as_the_url() {
        var psi = new ProcessStartInfo();

        ChildProfile.Apply(psi, Resolutions.At("https://override.test", Config.Root));

        await Assert.That(psi.Environment[ProfileOverrides.UrlVar]).IsEqualTo("https://override.test");
    }
}
