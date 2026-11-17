using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.Services.CliAdoption;

public class AdoptionIntegrationsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Runner(Func<ProcessResult> run) : IProcessRunner {
        public int Calls;
        public string? File;
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls++;
            File = fileName;
            return Task.FromResult(run());
        }
        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    static readonly ProcessResult Ok = new(0, "", "", false);

    ConfigRoot Config => new(Tmp.CreateDir("config"));
    string Bundle => Tmp.PathTo("Applications/kcap.app");
    string Cli => Path.Combine(Bundle, "Contents", "MacOS", "kcap");

    (AdoptionIntegrations Integrations, AdoptionRecordStore Store, FakeTimeProvider Time, ApplicationIntegrationRefresh Refresh)
        Build(Runner runner, FakeTimeProvider? time = null) {
        var config = Config;
        var store = new AdoptionRecordStore(config);
        var refresh = new ApplicationIntegrationRefresh(config, runner);
        time ??= new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        return (new AdoptionIntegrations(refresh, Cli, store, time), store, time, refresh);
    }

    [Test]
    public async Task The_first_completed_refresh_records_the_cutover_at_its_finish() {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        var runner = new Runner(() => { time.Advance(TimeSpan.FromSeconds(30)); return Ok; });
        var (integrations, store, _, _) = Build(runner, time);

        await Assert.That(await integrations.RepointAsync(null, CancellationToken.None)).IsTrue();

        await Assert.That(runner.File).IsEqualTo(Cli);
        await Assert.That(store.Load().CutoverUtc).IsEqualTo(new DateTime(2026, 11, 4, 10, 0, 30, DateTimeKind.Utc));
    }

    [Test]
    public async Task With_a_cutover_and_nothing_named_no_refresh_runs() {
        var runner = new Runner(() => Ok);
        var (integrations, store, _, _) = Build(runner);
        var cutover = new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        store.Update(r => r with { CutoverUtc = cutover });

        await Assert.That(await integrations.RepointAsync(null, CancellationToken.None)).IsTrue();

        await Assert.That(runner.Calls).IsEqualTo(0);
        await Assert.That(store.Load().CutoverUtc).IsEqualTo(cutover);
    }

    /// <summary>A rerun for a site naming the old install moves the cutover only when, asked after the refresh, the
    /// caller finds it repointed.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_rerun_moves_the_cutover_only_once_it_repointed_a_naming_site(bool repointed) {
        var runner = new Runner(() => Ok);
        var (integrations, store, time, _) = Build(runner);
        var cutover = new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        store.Update(r => r with { CutoverUtc = cutover });
        time.Advance(TimeSpan.FromMinutes(5));
        var askedAfter = -1;

        await Assert.That(await integrations.RepointAsync(_ => {
            askedAfter = runner.Calls;
            return Task.FromResult(repointed);
        }, CancellationToken.None)).IsTrue();

        await Assert.That(runner.Calls).IsEqualTo(1);
        await Assert.That(askedAfter).IsEqualTo(1);
        await Assert.That(store.Load().CutoverUtc).IsEqualTo(repointed ? new DateTime(2026, 11, 4, 10, 5, 0, DateTimeKind.Utc) : cutover);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_failed_refresh_records_no_cutover_and_returns_false(bool timedOut) {
        var runner = new Runner(() => new ProcessResult(timedOut ? 0 : 1, "", "boom", timedOut));
        var (integrations, store, _, _) = Build(runner);

        await Assert.That(await integrations.RepointAsync(null, CancellationToken.None)).IsFalse();

        await Assert.That(store.Load().CutoverUtc).IsNull();
    }

    [Test]
    public async Task A_pending_bundle_move_marker_for_this_bundle_is_consumed_by_the_adoption_refresh() {
        var runner = new Runner(() => Ok);
        var (integrations, _, _, refresh) = Build(runner);
        refresh.Schedule(Bundle);

        await Assert.That(await integrations.RepointAsync(null, CancellationToken.None)).IsTrue();
        await Assert.That(File.Exists(Config.Path(ApplicationIntegrationRefresh.FileName))).IsFalse();

        await refresh.RunPendingAsync(Bundle, CancellationToken.None);
        await Assert.That(runner.Calls).IsEqualTo(1);
    }
}
