using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit;

public class NeedToKnowMarksNudgeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task The_marks_are_taught_only_while_the_profile_has_the_experiment_on() {
        var config = new ConfigRoot(Tmp.Path);
        await Assert.That(NeedToKnowMarksNudge.Resolve(config, "default")).IsNull();

        await new ExperimentalSettingsStore(config).SetAsync("default", ExperimentalFeatures.NeedToKnowBasis.Id, true, CancellationToken.None);

        await Assert.That(NeedToKnowMarksNudge.Resolve(config, "default")).IsEqualTo(NeedToKnowMarksNudge.Text);
        await Assert.That(NeedToKnowMarksNudge.Resolve(config, "other")).IsNull();
    }
}
