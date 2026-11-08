using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Hosting;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The steering switch: the consent document's <c>steering</c> field, with the steering socket bound while it is on. It
/// moves only through <see cref="FrameType.SteeringSet"/>, since the daemon is the document's only writer, and the
/// socket follows it with no restart. A SteeringSet writes on only once the socket is up, and off before the socket
/// goes, so a failure on either side leaves the two agreeing.
/// </summary>
/// <param name="offered">Whether steering is offered on this platform; <see cref="SteeringIpc.Offered"/> unless given.</param>
internal sealed class SteeringSwitch(LaunchConsentStore store, SteeringEndpoint endpoint, bool? offered = null) : IHostedService, IDisposable {
    readonly SemaphoreSlim _lane = new(1, 1);
    readonly bool _offered = offered ?? SteeringIpc.Offered;

    /// <summary>Binds the socket when the switch was left on. One that won't bind stays on, and the next
    /// <see cref="SteeringIpc.On"/> tries again.</summary>
    public async Task StartAsync(CancellationToken ct) {
        if (!_offered) return;

        await _lane.WaitAsync(ct);

        try {
            if (store.SteeringOn) endpoint.TryBind(out _);
        } finally {
            _lane.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct) {
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
            } else {
                if (!store.TrySetSteering(false, out var unwritable)) return Refused(SteeringIpc.Unwritable, unwritable);

                await endpoint.UnbindAsync();
            }

            return new(true, null, State);
        } finally {
            _lane.Release();
        }
    }

    public void Dispose() => _lane.Dispose();

    string State => store.SteeringOn ? SteeringIpc.On : SteeringIpc.Off;

    SteeringSetAckDto Refused(string reason, string? detail = null) => new(false, reason, State, detail);
}
