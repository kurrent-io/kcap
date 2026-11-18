using Capacitor.Cli.Core.LocalIpc;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>The client's own reasons: no daemon to ask is a daemon that isn't running, and an answer it can't read is
/// one that didn't answer.</summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class SteeringIpcTests {
    const string DaemonName = "daemon-a";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task No_daemon_listening_is_unreachable() =>
        await Assert.That(await SteeringIpc.SetAsync(Daemons.Store, DaemonName, on: true, CancellationToken.None))
            .IsEqualTo(new SteeringSetAckDto(false, SteeringIpc.Unreachable, null));

    [Test]
    public async Task A_socket_path_too_long_to_open_is_unreachable() =>
        await Assert.That(await SteeringIpc.SetAsync(Daemons.Store, new string('n', 200), on: true, CancellationToken.None))
            .IsEqualTo(new SteeringSetAckDto(false, SteeringIpc.Unreachable, null));

    [Test]
    public async Task An_answer_it_cannot_read_is_unanswered() {
        await using var daemon = new FakeSteeringDaemon(Daemons.Store.SocketPath(DaemonName), answer: "not json");

        await Assert.That(await SteeringIpc.SetAsync(Daemons.Store, DaemonName, on: true, CancellationToken.None))
            .IsEqualTo(new SteeringSetAckDto(false, SteeringIpc.Unanswered, null));
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On]);
    }
}
