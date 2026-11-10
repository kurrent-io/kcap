using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

/// <summary>The mode a local server last applied, bound to one installation and read as no mode whenever the file cannot
/// say, so an installation nobody answered for gets no model calls.</summary>
public class LocalIntelligenceRecordTests {
    const string Installation = "0123456789abcdef0123456789abcdef";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task A_mode_round_trips_for_its_installation() {
        var saved = new LocalIntelligenceRecord(Installation, "provider");

        await Assert.That(saved.Save(Config.Root)).IsTrue();
        await Assert.That(LocalIntelligenceRecord.For(Config.Root, Installation)).IsEqualTo(saved);
    }

    [Test]
    public async Task Another_installation_reads_as_no_mode() {
        new LocalIntelligenceRecord(Installation, "local_agent").Save(Config.Root);

        await Assert.That(LocalIntelligenceRecord.For(Config.Root, "fedcba9876543210fedcba9876543210").Mode).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("not json")]
    [Arguments("""{"installation_id":"0123456789abcdef0123456789abcdef","mode":7}""")]
    public async Task A_missing_or_unreadable_file_reads_as_no_mode(string? content) {
        if (content is not null) Config.CreateFile(LocalIntelligenceRecord.FileName, content);

        await Assert.That(LocalIntelligenceRecord.For(Config.Root, Installation)).IsEqualTo(new LocalIntelligenceRecord(Installation));
    }

    [Test]
    [Arguments("local_agent", true)]
    [Arguments("provider", true)]
    [Arguments("none", false)]
    [Arguments("something_new", false)]
    [Arguments(null, false)]
    public async Task Only_local_agent_and_provider_run_models(string? mode, bool runs) =>
        await Assert.That(new LocalIntelligenceRecord(Installation, mode).RunsModels).IsEqualTo(runs);
}
