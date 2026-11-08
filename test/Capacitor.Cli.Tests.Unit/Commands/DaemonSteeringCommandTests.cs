using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>kcap daemon steering on|off</c> and <c>kcap daemon steering-socket</c>, against control and steering
/// sockets standing in for a daemon's.</summary>
public class DaemonSteeringCommandTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }

    const string Name = "t";

    StringWriter Out { get; } = new();
    StringWriter Err { get; } = new();

    StandardStreams Streams => new(Out, Err, TextReader.Null);

    ProfileContext Profiles => Resolutions.None(Config.Root);

    DaemonCommands Daemon() => new(
        Daemons.Store, Config.Root, Profiles, Home, TestHarnesses.All(), TestBinaries.None, TimeProvider.System, Streams);

    [Test]
    public async Task Steering_is_refused_on_windows() {
        var exit = await DaemonSteeringCommand.SetAsync(Daemons.Store, Profiles, TimeProvider.System, Streams, ["on", "--name", Name], offered: false);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains("isn't offered on Windows");
        await Assert.That(Err.ToString()).DoesNotContain("not running");
        await Assert.That(Out.ToString()).IsEmpty();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // refused there before any daemon is asked
    public async Task Steering_on_says_when_no_daemon_is_running() {
        var exit = await Daemon().HandleAsync(["daemon", "steering", "on", "--name", Name]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains($"daemon '{Name}' is not running");
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_on_says_what_the_daemon_set() {
        await using var daemon = new SwitchDaemon(Daemons.Store.SocketPath(Name), new SteeringSetAckDto(true, null, SteeringIpc.On));

        var exit = await Daemon().HandleAsync(["daemon", "steering", "on", "--name", Name]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Out.ToString()).IsEqualTo($"Steering is on for daemon '{Name}'.{Out.NewLine}");
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On]);
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_off_names_a_daemon_too_old_for_the_switch() {
        await using var daemon = new SwitchDaemon(Daemons.Store.SocketPath(Name), ack: null);

        var exit = await Daemon().HandleAsync(["daemon", "steering", "off", "--name", Name]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains("doesn't support steering");
        await Assert.That(daemon.Asked).IsEmpty();
    }

    /// <summary>The mod runs this to find the daemon, so a daemon that can't be steered must leave stdout empty
    /// rather than say why.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_socket_prints_nothing_when_off() {
        var path = Daemons.Store.SteeringSocketPath(Name);

        // Never bound: the switch is off, or no daemon of this name ever turned it on.
        await Assert.That(await Daemon().HandleAsync(["daemon", "steering-socket", "--name", Name])).IsEqualTo(0);

        // A socket file nothing listens on: a daemon that died with the switch on. A closed socket unlinks the file it
        // bound, so the file is moved out from under it first.
        Daemons.CreateDir(Name);
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)) {
            dead.Bind(new UnixDomainSocketEndPoint(path + ".bound"));
            File.Move(path + ".bound", path);
        }
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(await Daemon().HandleAsync(["daemon", "steering-socket", "--name", Name])).IsEqualTo(0);

        // Listening, on a platform where steering isn't offered.
        File.Delete(path);
        using var live = Listen(path);
        await Assert.That(await DaemonSteeringCommand.PrintSocketAsync(Daemons.Store, Profiles, TimeProvider.System, Streams, ["--name", Name], offered: false))
            .IsEqualTo(0);

        await Assert.That(Out.ToString()).IsEmpty();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_socket_prints_the_path_alone_while_the_daemon_listens() {
        var path = Daemons.Store.SteeringSocketPath(Name);
        Daemons.CreateDir(Name);
        using var live = Listen(path);

        var exit = await Daemon().HandleAsync(["daemon", "steering-socket", "--name", Name]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Out.ToString()).IsEqualTo(path + Out.NewLine);
        await Assert.That(Err.ToString()).IsEmpty();
    }

    static Socket Listen(string path) {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(1);

        return socket;
    }

    /// <summary>A control socket whose hello offers the switch when it has an <c>ack</c> to give, and which answers
    /// each SteeringSet with it, one connection at a time.</summary>
    sealed class SwitchDaemon : IAsyncDisposable {
        readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource _cts = new();
        readonly List<string> _asked = [];
        readonly Task _serving;

        public SwitchDaemon(string socketPath, SteeringSetAckDto? ack) {
            _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            _listener.Listen(4);
            _serving = Task.Run(() => ServeAsync(ack));
        }

        /// <summary>The payload of every SteeringSet it read.</summary>
        public IReadOnlyList<string> Asked { get { lock (_asked) return [.. _asked]; } }

        async Task ServeAsync(SteeringSetAckDto? ack) {
            var hello = JsonSerializer.Serialize(
                new HelloReplyDto(HelloProtocol.CurrentVersion, "test", Name, ack is null ? [] : [SteeringIpc.SwitchCapability]),
                HelloIpcJsonContext.Default.HelloReplyDto);

            try {
                while (true) {
                    using var conn = await _listener.AcceptAsync(_cts.Token);
                    await using var stream = new NetworkStream(conn, ownsSocket: false);
                    if (await FrameCodec.ReadAsync(stream, _cts.Token) is not { } frame) continue;

                    if (frame.Type == FrameType.Hello) {
                        await FrameCodec.WriteAsync(stream, LocalFrame.HelloJson(FrameType.HelloReply, hello), _cts.Token);

                        continue;
                    }

                    lock (_asked) _asked.Add(frame.Text);
                    var json = JsonSerializer.Serialize(ack!, SteeringIpcJsonContext.Default.SteeringSetAckDto);
                    await FrameCodec.WriteAsync(stream, LocalFrame.SteeringSetAck(json), _cts.Token);
                }
            } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException) {
                // Disposed: the listener is gone.
            }
        }

        public async ValueTask DisposeAsync() {
            await _cts.CancelAsync();
            _listener.Dispose();
            await _serving;
            _cts.Dispose();
        }
    }
}
