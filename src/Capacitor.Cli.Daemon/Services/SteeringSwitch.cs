using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Hosting;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The steering switch: the consent document's <c>steering</c> field, with the steering socket bound while it is on. It
/// moves only through <see cref="FrameType.SteeringSet"/>, since the daemon is the document's only writer, and the
/// socket follows it with no restart. A SteeringSet writes on only once the socket is up, and off before the socket
/// goes, so a failure on either side leaves the two agreeing. While it is on, a socket file that goes from under it is
/// bound again within <see cref="RebindInterval"/>, since every mod keeps trying that path.
/// </summary>
/// <param name="offered">Whether steering is offered on this platform; <see cref="SteeringIpc.Offered"/> unless given.</param>
/// <param name="attachments">The sessions steered while the switch is on, every one of them ended when it goes off.</param>
internal sealed class SteeringSwitch(
        LaunchConsentStore store, SteeringEndpoint endpoint, bool? offered = null, Harness.Claude.ModAttachments? attachments = null,
        TimeProvider? time = null
    ) : IHostedService, IDisposable {
    internal static readonly TimeSpan RebindInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long switching off waits for the requests the socket was serving. Under the CLI's 10 s wait for the
    /// answer: a request stuck on the server, as an attach is for its 15 s registration budget, would outlast it.</summary>
    internal static readonly TimeSpan UnbindBound = TimeSpan.FromSeconds(5);

    readonly SemaphoreSlim _lane = new(1, 1);
    readonly bool _offered = offered ?? SteeringIpc.Offered;
    ITimer? _rebind;

    // Whether the socket has bound since the switch came on; read and written in the lane. The endpoint's IsBound can't
    // stand in for it: a rebind that fails leaves the endpoint unbound, and the next round would skip it.
    bool _served;

    /// <summary>Binds the socket when the switch was left on. One that won't bind stays on, and the next
    /// <see cref="SteeringIpc.On"/> tries again.</summary>
    public async Task StartAsync(CancellationToken ct) {
        if (!_offered) return;

        await _lane.WaitAsync(ct);

        try {
            if (store.SteeringOn) _served = endpoint.TryBind(out _);
        } finally {
            _lane.Release();
        }

        _rebind = (time ?? TimeProvider.System).CreateTimer(_ => Rebind(), null, RebindInterval, RebindInterval);
    }

    // Skipped while the switch moves. A socket that never bound waits for the next on, as at start.
    void Rebind() {
        try {
            if (!_lane.Wait(0)) return;

            try {
                if (store.SteeringOn && _served) endpoint.TryBind(out _);
            } catch {
                // The next round tries again.
            } finally {
                _lane.Release();
            }
        } catch (ObjectDisposedException) {
            // Dispose doesn't wait for a running tick, and a throw here would be unhandled on the timer thread.
        }
    }

    public async Task StopAsync(CancellationToken ct) {
        if (_rebind is { } rebind) await rebind.DisposeAsync();

        await _lane.WaitAsync(ct);

        try {
            await endpoint.UnbindAsync();
        } finally {
            _lane.Release();
        }
    }

    public async Task HandleSetAsync(string payload, Stream stream, CancellationToken ct) {
        var ack = payload switch {
            SteeringIpc.On  => await SetAsync(on: true, ct),
            SteeringIpc.Off => await SetAsync(on: false, ct),
            _               => Refused(SteeringIpc.Malformed),
        };

        var json = JsonSerializer.Serialize(ack, SteeringIpcJsonContext.Default.SteeringSetAckDto);
        await FrameCodec.WriteAsync(stream, LocalFrame.SteeringSetAck(json), ct);
    }

    async Task<SteeringSetAckDto> SetAsync(bool on, CancellationToken ct) {
        if (!_offered) return Refused(SteeringIpc.NotOffered);

        await _lane.WaitAsync(ct);

        try {
            if (on) {
                var wasBound = endpoint.IsBound;
                if (!endpoint.TryBind(out var unbindable)) return Refused(SteeringIpc.Unbindable, unbindable);

                if (!store.TrySetSteering(true, out var unwritable)) {
                    if (!wasBound) await endpoint.UnbindAsync();

                    return Refused(SteeringIpc.Unwritable, unwritable);
                }

                _served = true;
            } else {
                if (!store.TrySetSteering(false, out var unwritable)) return Refused(SteeringIpc.Unwritable, unwritable);

                // Ahead of the socket, which waits for requests already in: a handover answered meanwhile then finds its
                // attachment gone, and is reported back rather than given to a mod.
                attachments?.EndAll();

                try {
                    await endpoint.UnbindAsync().WaitAsync(UnbindBound, time ?? TimeProvider.System, CancellationToken.None);
                } catch (TimeoutException) {
                    // The socket is closed and its file gone already; what it was serving finishes on its own.
                }

                _served = false;
            }

            return new(true, null, State);
        } finally {
            _lane.Release();
        }
    }

    public void Dispose() {
        _rebind?.Dispose();
        _lane.Dispose();
    }

    string State => store.SteeringOn ? SteeringIpc.On : SteeringIpc.Off;

    SteeringSetAckDto Refused(string reason, string? detail = null) => new(false, reason, State, detail);
}
