namespace Capacitor.Cli.Daemon.Tests.Unit;

public class DaemonConfigRepoAllowedTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_launch_path_is_allowed_by_where_it_resolves() {
        var allowed = Tmp.CreateDir("allowed", "proj");
        Tmp.CreateDir("outside");
        var config = new DaemonConfig { AllowedRepoPaths = [Tmp.PathTo("allowed") + "/*"] };

        await Assert.That(config.IsRepoAllowed(Tmp.PathTo("allowed", "..", "outside"))).IsFalse();
        await Assert.That(config.IsRepoAllowed(allowed)).IsTrue();
    }

}
