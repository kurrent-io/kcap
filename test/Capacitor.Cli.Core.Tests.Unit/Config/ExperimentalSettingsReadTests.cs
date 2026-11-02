using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

public class ExperimentalSettingsReadTests {
    static readonly ExperimentalFeature DefaultOn  = new("f", "F", "d", AppliesLive: true, OnByDefault: true);
    static readonly ExperimentalFeature DefaultOff = new("f", "F", "d", AppliesLive: true);

    static ExperimentalSettingsRead Saved(bool? value) =>
        new(true, value is { } v ? new Dictionary<string, bool> { ["f"] = v } : new Dictionary<string, bool>(), null);

    [Test]
    [Arguments(null, true, true)]
    [Arguments(null, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, true)]
    public async Task A_saved_value_wins_and_an_unset_switch_takes_the_feature_default(bool? saved, bool defaultOn, bool expected) {
        var read = Saved(saved);

        await Assert.That(read.IsOn(defaultOn ? DefaultOn : DefaultOff)).IsEqualTo(expected);
        await Assert.That(read.IsSet("f")).IsEqualTo(saved is not null);
    }
}
