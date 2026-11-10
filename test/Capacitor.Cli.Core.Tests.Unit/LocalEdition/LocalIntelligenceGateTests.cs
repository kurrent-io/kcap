using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

/// <summary>Client-side model calls go ahead for any server that is not a local installation, and for an installation only
/// once its server applied an answer that runs a model.</summary>
public class LocalIntelligenceGateTests {
    const string Local        = "http://localhost:41999";
    const string Installation = "inst-1";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    LocalIntelligenceGate Gate(LocalEndpoints endpoints) => new(Config.Root, () => endpoints);

    [Test]
    [Arguments("https://acme.kcap.ai")]
    [Arguments(Local)]
    public async Task A_server_that_is_not_an_installation_is_allowed(string server) =>
        await Assert.That(Gate(NoLocalInstallation.Endpoints).Allows(server)).IsTrue();

    [Test]
    public async Task An_installation_with_no_record_is_skipped() =>
        await Assert.That(Gate(Resolutions.InstalledAt(Local, Installation)).Allows(Local)).IsFalse();

    [Test]
    [Arguments("local_agent", true)]
    [Arguments("provider", true)]
    [Arguments("none", false)]
    public async Task An_installation_is_allowed_only_for_a_mode_that_runs_models(string mode, bool allowed) {
        new LocalIntelligenceRecord(Installation, mode).Save(Config.Root);

        await Assert.That(Gate(Resolutions.InstalledAt(Local, Installation)).Allows(Local)).IsEqualTo(allowed);
    }

    /// <summary>Another spelling of the loopback origin is the same installation.</summary>
    [Test]
    public async Task Any_loopback_spelling_of_the_installation_reads_its_record() {
        new LocalIntelligenceRecord(Installation, "provider").Save(Config.Root);

        await Assert.That(Gate(Resolutions.InstalledAt(Local, Installation)).Allows("http://127.0.0.1:41999")).IsTrue();
    }

    [Test]
    public async Task A_record_for_another_installation_is_skipped() {
        new LocalIntelligenceRecord("inst-0", "provider").Save(Config.Root);

        await Assert.That(Gate(Resolutions.InstalledAt(Local, Installation)).Allows(Local)).IsFalse();
    }

    /// <summary>A purged installation's origin, or a loopback one while the registry cannot be read, may still be an
    /// installation, so even a record that would allow it does not.</summary>
    [Test]
    public async Task A_retired_or_undetermined_origin_is_skipped() {
        new LocalIntelligenceRecord("inst-0", "provider").Save(Config.Root);

        await Assert.That(Gate(Resolutions.RetiredAt(Local)).Allows(Local)).IsFalse();
        await Assert.That(Gate(Resolutions.UnreadableRegistry).Allows(Local)).IsFalse();
    }
}
