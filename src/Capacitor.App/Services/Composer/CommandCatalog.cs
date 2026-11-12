using System.Collections.Frozen;
using Capacitor.App.ViewModels.Composer;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services.Composer;

/// The commands a composer's / offers: a running agent's own, from its daemon, else what its harness
/// reports before launch. A harness gets a pre-launch list by adding its probe to <see cref="Probes"/>.
public sealed class CommandCatalog {
    internal delegate Task<IReadOnlyList<HostedAgentCommand>> Probe(string directory, CancellationToken ct);

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    readonly ILocalControlOps _ops;
    readonly IReadOnlyDictionary<HarnessId, Probe> _probes;
    readonly Dictionary<(string Directory, HarnessId Harness), Task<IReadOnlyList<HostedAgentCommand>>> _probed = [];
    readonly CancellationToken _shutdown;
    volatile bool _daemonLists;

    public CommandCatalog(ILocalControlOps ops, IObservable<AttachStatus> daemon, UserHome home, TimeProvider time, CancellationToken shutdown)
        : this(ops, daemon, Probes(HarnessRegistry.FromEnvironment(home), home, time), shutdown) { }

    internal CommandCatalog(ILocalControlOps ops, IObservable<AttachStatus> daemon, IReadOnlyDictionary<HarnessId, Probe> probes, CancellationToken shutdown) {
        _ops = ops;
        _shutdown = shutdown;
        _probes = probes;
        daemon.Subscribe(s => _daemonLists = s.State == AttachState.Connected && s.Capabilities?.Contains(AgentCommandsIpc.Capability) == true);
    }

    static FrozenDictionary<HarnessId, Probe> Probes(HarnessRegistry harnesses, UserHome home, TimeProvider time) =>
        new Dictionary<HarnessId, Probe> {
            [HarnessId.Claude] = (directory, ct) => ClaudeCommandProbe.ProbeAsync(harnesses, directory, null, home, time, ProbeTimeout, Log, ct),
        }.ToFrozenDictionary();

    /// A probe's list is kept per checkout, since each one starts the harness; an empty one is probed again.
    /// Called on the UI thread only.
    public async Task<IReadOnlyList<HostedAgentCommand>> ListAsync(ComposerContext context) {
        if (context.AgentId is { } agentId && _daemonLists && await _ops.ListCommandsAsync(agentId, _shutdown) is { Count: > 0 } listed)
            return listed;
        if (context.Harness is not { } harness || !_probes.TryGetValue(harness, out var probe)) return [];

        var key = (context.Directory, harness);
        if (!_probed.TryGetValue(key, out var probed) || probed is { IsCompletedSuccessfully: true, Result.Count: 0 })
            _probed[key] = probed = Task.Run(() => probe(context.Directory, _shutdown));

        return await probed;
    }

    static void Log(string message) => Console.Error.WriteLine($"kcap: {message}");
}
