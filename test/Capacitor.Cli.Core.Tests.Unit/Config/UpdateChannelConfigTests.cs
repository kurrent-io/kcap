using System.Text.Json;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

public class UpdateChannelConfigTests {
    // Construct directly to pin the record initializer. Deserializing an absent property can yield null,
    // which the config read site separately handles with ?? "latest".
    [Test]
    public async Task Defaults_to_latest_on_new_profile() {
        await Assert.That(new Profile().UpdateChannel).IsEqualTo("latest");
    }

    // Round-trip through the SAME serialization context the profile config uses on
    // disk (ProfileConfigJsonContext[Indented]).
    [Test]
    public async Task Round_trips_beta_through_profile_config() {
        var config = new ProfileConfig {
            Profiles = new() { ["default"] = new Profile { UpdateChannel = "beta" } }
        };
        var json = JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig);
        await Assert.That(json).Contains("update_channel");
        await Assert.That(json).Contains("beta");
        var back = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;
        await Assert.That(back.Profiles["default"].UpdateChannel).IsEqualTo("beta");
    }
}
