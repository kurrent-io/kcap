using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class BearerRefusalLogTests {
    [Test]
    public async Task One_bearer_is_reported_once_however_often_it_is_refused() {
        var warned = new List<string>();
        var log    = new BearerRefusalLog(warned.Add);

        log.Presenting("token");
        log.Refused("401 at noon");
        log.Presenting("token");
        log.Refused("401 half a minute later");

        await Assert.That(warned).IsEquivalentTo(["401 at noon"]);
    }

    /// <summary>A new sign-in that is refused is news.</summary>
    [Test]
    public async Task Another_bearer_is_reported_again() {
        var warned = new List<string>();
        var log    = new BearerRefusalLog(warned.Add);

        log.Presenting("token");
        log.Refused("first");
        log.Presenting("another");
        log.Refused("second");

        await Assert.That(warned).IsEquivalentTo(["first", "second"]);
    }
}
