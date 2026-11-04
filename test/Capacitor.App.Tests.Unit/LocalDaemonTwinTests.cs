using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class LocalDaemonTwinTests {
    static DaemonInfo D(string name, string? machineId, string owner = "u1") =>
        new() { Name = name, MachineId = machineId, OwnerUserId = owner, Connected = true };

    const string Server = "https://cap.example.com";

    [Test]
    public async Task ExactlyOneMatchWins() {
        var twin = LocalDaemonTwin.Find([D("work-mac", "m1"), D("work-mac", "m2", "u2")], "m1", "work-mac", Server, Server);
        await Assert.That(twin).IsEqualTo(("u1", "work-mac"));
    }

    [Test]
    public async Task TwoCandidatesFailOpen() {
        var twin = LocalDaemonTwin.Find([D("work-mac", "m1"), D("work-mac", "m1", "u2")], "m1", "work-mac", Server, Server);
        await Assert.That(twin).IsNull();
    }

    [Test]
    public async Task MissingMachineIdFailsOpen() {
        await Assert.That(LocalDaemonTwin.Find([D("work-mac", null)], "m1", "work-mac", Server, Server)).IsNull();
        await Assert.That(LocalDaemonTwin.Find([D("work-mac", "m1")], null, "work-mac", Server, Server)).IsNull();
    }

    [Test]
    public async Task DifferentServerNeverMatches() {
        var twin = LocalDaemonTwin.Find([D("work-mac", "m1")], "m1", "work-mac", "https://other.example.com", Server);
        await Assert.That(twin).IsNull();
    }

    /// A reconnect can list the departing connection beside the new one; both are the same daemon.
    [Test]
    public async Task OneIdentityListedTwiceIsOneTwin() {
        var twin = LocalDaemonTwin.Find([D("work-mac", "m1"), D("work-mac", "m1")], "m1", "work-mac", Server, Server);
        await Assert.That(twin).IsEqualTo(("u1", "work-mac"));
    }

    /// A daemon restarting leaves the registry for a while; its proven identity still names its rows.
    [Test]
    public async Task AProvenTwinStandsWhileTheRegistryDoesNotListIt() {
        var twin = LocalDaemonTwin.Retained(("u1", "work-mac"), [D("home-pc", "m2")], "m1", "work-mac", Server, Server);
        await Assert.That(twin).IsEqualTo(("u1", "work-mac"));
    }

    [Test]
    public async Task AProvenTwinLapsesOnceAnotherMachineHoldsItsSlotOrAnotherOwnerHoldsThisMachine() {
        await Assert.That(LocalDaemonTwin.Retained(("u1", "work-mac"), [D("work-mac", "m2")], "m1", "work-mac", Server, Server)).IsNull();
        await Assert.That(LocalDaemonTwin.Retained(("u1", "work-mac"), [D("work-mac", "m1", "u2")], "m1", "work-mac", Server, Server)).IsNull();
        await Assert.That(LocalDaemonTwin.Retained(("u1", "work-mac"), [], "m1", "work-mac", "https://other.example.com", Server)).IsNull();
        await Assert.That(LocalDaemonTwin.Retained(("u1", "work-mac"), [], "m1", "renamed", Server, Server)).IsNull();
    }

    /// Another user's daemon of the same name on another machine is a different slot and says nothing.
    [Test]
    public async Task AnotherOwnersSameNameElsewhereLeavesTheTwinStanding() {
        var twin = LocalDaemonTwin.Retained(("u1", "work-mac"), [D("work-mac", "m2", "u2")], "m1", "work-mac", Server, Server);
        await Assert.That(twin).IsEqualTo(("u1", "work-mac"));
    }
}
