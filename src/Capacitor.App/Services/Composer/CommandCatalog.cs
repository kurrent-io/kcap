using System.Collections.Frozen;
using Capacitor.App.ViewModels.Composer;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services.Composer;

public sealed class CommandCatalog {
    internal delegate Task<IReadOnlyList<HostedAgentCommand>> Probe(string directory, CancellationToken ct);

    internal sealed record Source(Probe Probe, char Sigil = '/');

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    readonly ILocalControlOps _ops;
    readonly IReadOnlyDictionary<HarnessId, Source> _sources;
    readonly Dictionary<(string Directory, HarnessId Harness), Task<IReadOnlyList<HostedAgentCommand>>> _probed = [];
    readonly CancellationToken _shutdown;
    volatile bool _daemonLists;

    public CommandCatalog(ILocalControlOps ops, IObservable<AttachStatus> daemon, UserHome home, TimeProvider time, CancellationToken shutdown)
        : this(ops, daemon, Sources(HarnessRegistry.FromEnvironment(home), home, time), shutdown) { }

    internal CommandCatalog(ILocalControlOps ops, IObservable<AttachStatus> daemon, IReadOnlyDictionary<HarnessId, Source> sources, CancellationToken shutdown) {
        _ops = ops;
        _shutdown = shutdown;
        _sources = sources;
        daemon.Subscribe(s => _daemonLists = s.State == AttachState.Connected && s.Capabilities?.Contains(AgentCommandsIpc.Capability) == true);
    }

    static FrozenDictionary<HarnessId, Source> Sources(HarnessRegistry harnesses, UserHome home, TimeProvider time) =>
        new Dictionary<HarnessId, Source> {
            [HarnessId.Claude] = new((directory, ct) => ClaudeCommandProbe.ProbeAsync(harnesses, directory, null, home, time, ProbeTimeout, Log, ct)),
            [HarnessId.Codex] = new((directory, ct) => CodexSkillProbe.ProbeAsync(harnesses, directory, home, time, ProbeTimeout, Log, ct), CodexSkills.Sigil),
            [HarnessId.Pi] = new((directory, ct) => PiCommandProbe.ProbeAsync(harnesses, directory, time, ProbeTimeout, Log, ct)),
        }.ToFrozenDictionary();

    public char SigilOf(HarnessId? harness) => harness is { } known && _sources.TryGetValue(known, out var source) ? source.Sigil : '/';

    /// UI thread only. A probe's list is kept per checkout, since each probe starts the harness.
    public async Task<IReadOnlyList<HostedAgentCommand>> ListAsync(ComposerContext context) {
        if (context.AgentId is { } agentId && _daemonLists && await _ops.ListCommandsAsync(agentId, _shutdown) is { Count: > 0 } listed)
            return listed;
        if (context.Harness is not { } harness || !_sources.TryGetValue(harness, out var source)) return [];

        var key = (context.Directory, harness);
        if (!_probed.TryGetValue(key, out var probed) || probed is { IsCompletedSuccessfully: true, Result.Count: 0 })
            _probed[key] = probed = Task.Run(() => source.Probe(context.Directory, _shutdown));

        return await probed;
    }

    static void Log(string message) => Console.Error.WriteLine($"kcap: {message}");
}
