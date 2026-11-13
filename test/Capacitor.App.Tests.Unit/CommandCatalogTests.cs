using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.Composer;
using Capacitor.App.ViewModels.Composer;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

public class CommandCatalogTests {
    static readonly HostedAgentCommand Listed = new("from-daemon", null, null);
    static readonly HostedAgentCommand Probed = new("from-probe", null, null);

    static CommandCatalog Catalog(ScriptedLocalControlOps ops, string[] capabilities, Func<IReadOnlyList<HostedAgentCommand>> probe, List<string> probes) =>
        new(ops, Observable.Return(new AttachStatus(AttachState.Connected, null, capabilities)),
            new Dictionary<HarnessId, CommandCatalog.Source> {
                [HarnessId.Claude] = new((directory, account, _) => {
                    probes.Add(account is null ? directory : $"{directory} as {account.Directory}");
                    return Task.FromResult(probe());
                }),
            }, CancellationToken.None);

    [Test]
    public async Task A_running_agent_lists_from_its_daemon_without_probing() {
        var probes = new List<string>();
        var catalog = Catalog(new ScriptedLocalControlOps { ListCommands = _ => [Listed] }, [AgentCommandsIpc.Capability], () => [Probed], probes);

        var commands = await catalog.ListAsync(new ComposerContext("/wt", HarnessId.Claude, "a1"));

        await Assert.That(commands).IsEquivalentTo([Listed]);
        await Assert.That(probes).IsEmpty();
    }

    [Test]
    public async Task A_daemon_without_the_capability_falls_back_to_one_kept_probe_per_checkout_and_account() {
        var probes = new List<string>();
        var catalog = Catalog(new ScriptedLocalControlOps { ListCommands = _ => [Listed] }, [], () => [Probed], probes);

        await catalog.ListAsync(new ComposerContext("/wt", HarnessId.Claude, "a1"));
        var commands = await catalog.ListAsync(new ComposerContext("/wt", HarnessId.Claude));
        await catalog.ListAsync(new ComposerContext("/other", HarnessId.Claude));
        await catalog.ListAsync(new ComposerContext("/wt", HarnessId.Claude, Account: new ResolvedAccount("/acct", "id", "work")));

        await Assert.That(commands).IsEquivalentTo([Probed]);
        await Assert.That(probes).IsEquivalentTo(["/wt", "/other", "/wt as /acct"]);
    }

    [Test]
    public async Task An_empty_probe_is_probed_again_and_an_unprobed_harness_lists_nothing() {
        var probes = new List<string>();
        var catalog = Catalog(new ScriptedLocalControlOps(), [], () => [], probes);

        await catalog.ListAsync(new ComposerContext("/wt", HarnessId.Claude));
        await catalog.ListAsync(new ComposerContext("/wt", HarnessId.Claude));
        var codex = await catalog.ListAsync(new ComposerContext("/wt", HarnessId.Codex));

        await Assert.That(probes).Count().IsEqualTo(2);
        await Assert.That(codex).IsEmpty();
    }
}
