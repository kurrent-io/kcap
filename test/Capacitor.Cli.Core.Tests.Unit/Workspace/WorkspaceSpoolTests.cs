using System.Net;
using Capacitor.Cli.Core.Workspace;

namespace Capacitor.Cli.Core.Tests.Unit.Workspace;

public class WorkspaceSpoolTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    const string Server = "https://kcap.example";
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    WorkspaceSpool SpoolFor(string server = Server, long cap = WorkspaceSpool.DefaultCapBytes, List<string>? warnings = null) =>
        new(SpoolPartition.Server(Config.Root.Path(WorkspaceSpool.DirectoryName), server), _time, w => warnings?.Add(w), cap);

    static WorkspaceChangesRequest Span(string session, int changes = 1) => new(
        session, Guid.NewGuid(), "/repo", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        Enumerable.Range(0, changes).Select(i => new WorkspaceChange($"file-{i}.txt", "edit")).ToList(), false);

    const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    static List<string> Files(WorkspaceSpool spool) =>
        Directory.Exists(spool.DirectoryPath) ? Directory.EnumerateFiles(spool.DirectoryPath, "*.json", SearchOption.AllDirectories).ToList() : [];

    /// <summary>Spools each span with a file time a minute after the one before.</summary>
    static void WriteInOrder(WorkspaceSpool spool, params WorkspaceChangesRequest[] spans) {
        var start = DateTime.UtcNow - TimeSpan.FromHours(1);
        for (var i = 0; i < spans.Length; i++) {
            spool.Write(spans[i]);
            File.SetLastWriteTimeUtc(Files(spool).Single(f => f.Contains(spans[i].SpanId.ToString("N"))), start + TimeSpan.FromMinutes(i));
        }
    }

    [Test]
    public async Task Making_room_at_the_cap_deletes_the_oldest_span_files_whole() {
        var warnings = new List<string>();
        var probe    = SpoolFor();
        probe.Write(Span(A, changes: 40));
        var oneFile  = new FileInfo(Files(probe).Single()).Length;
        Directory.Delete(probe.DirectoryPath, recursive: true);

        var spool = SpoolFor(cap: oneFile * 3, warnings: warnings);
        var spans = Enumerable.Range(0, 5).Select(_ => Span(A, changes: 40)).ToArray();
        foreach (var span in spans) {
            spool.Write(span);
            File.SetLastWriteTimeUtc(Files(spool).Single(f => f.Contains(span.SpanId.ToString("N"))), _time.GetUtcNow().UtcDateTime);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var kept = Files(spool).Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToList();
        await Assert.That(kept).IsEquivalentTo(spans.TakeLast(3).Select(s => s.SpanId.ToString("N")).Order().ToList());
        await Assert.That(warnings.Count).IsEqualTo(2);
        foreach (var file in Files(spool))
            await Assert.That(() => System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(file), WorkspaceJsonContext.Default.WorkspaceChangesRequest)).ThrowsNothing();
    }

    [Test]
    public async Task A_failed_post_stays_spooled_and_the_next_drain_delivers_it() {
        var spool = SpoolFor();
        spool.Write(Span(A));
        var down  = new ScriptedRoute(_ => HttpStatusCode.ServiceUnavailable);
        var up    = new ScriptedRoute(_ => HttpStatusCode.Accepted);

        await Assert.That(await spool.DrainAsync(down.Client(), Server, Budget)).IsEqualTo(WorkspaceDrainStop.Retryable);
        await Assert.That(Files(spool)).HasSingleItem();

        await Assert.That(await spool.DrainAsync(up.Client(), Server, Budget)).IsEqualTo(WorkspaceDrainStop.Drained);
        await Assert.That(Files(spool)).IsEmpty();
        await Assert.That(up.Posted).IsEquivalentTo([A]);
    }

    [Test]
    public async Task A_401_keeps_the_file_and_stops_the_pass() {
        var spool = SpoolFor();
        WriteInOrder(spool, Span(A), Span(B));
        var route = new ScriptedRoute(_ => HttpStatusCode.Unauthorized);

        await Assert.That(await spool.DrainAsync(route.Client(), Server, Budget)).IsEqualTo(WorkspaceDrainStop.Retryable);

        await Assert.That(route.Posted).HasSingleItem();
        await Assert.That(Files(spool).Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_rejected_payload_is_dropped_and_the_pass_goes_on() {
        var spool = SpoolFor();
        WriteInOrder(spool, Span(A), Span(B));
        var route = new ScriptedRoute(session => session == A ? HttpStatusCode.BadRequest : HttpStatusCode.OK);

        await spool.DrainAsync(route.Client(), Server, Budget);

        await Assert.That(route.Posted).IsEquivalentTo([A, B]);
        await Assert.That(Files(spool)).IsEmpty();
    }

    [Test]
    public async Task Spans_drain_oldest_first() {
        var spool = SpoolFor();
        WriteInOrder(spool, Span(B), Span(A));
        var route = new ScriptedRoute(_ => HttpStatusCode.OK);

        await spool.DrainAsync(route.Client(), Server, Budget);

        await Assert.That(route.Posted).IsEquivalentTo([B, A], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Two_servers_never_see_each_others_spans() {
        var first  = SpoolFor("https://one.example");
        var second = SpoolFor("https://two.example");
        first.Write(Span(A));
        second.Write(Span(B));
        var route  = new ScriptedRoute(_ => HttpStatusCode.OK);

        await first.DrainAsync(route.Client(), "https://one.example", Budget);

        await Assert.That(route.Posted).IsEquivalentTo([A]);
        await Assert.That(Files(second)).HasSingleItem();
    }

    [Test]
    public async Task A_server_without_the_route_is_paused_for_a_day_and_drained_after_its_upgrade() {
        var spool = SpoolFor();
        spool.Write(Span(A));
        var old   = new ScriptedRoute(_ => HttpStatusCode.NotFound, () => HttpStatusCode.NotFound);

        await Assert.That(await spool.DrainAsync(old.Client(), Server, Budget)).IsEqualTo(WorkspaceDrainStop.RouteMissing);
        await Assert.That(spool.IsPaused).IsTrue();

        // Turns keep spooling during the pause, and no drain reaches the server.
        spool.Write(Span(B));
        await Assert.That(await spool.DrainAsync(old.Client(), Server, Budget)).IsEqualTo(WorkspaceDrainStop.Paused);
        await Assert.That(old.Posted.Count).IsEqualTo(1);

        _time.Advance(WorkspaceSpool.RouteMissingPause + TimeSpan.FromMinutes(1));
        var upgraded = new ScriptedRoute(_ => HttpStatusCode.OK);
        await Assert.That(await spool.DrainAsync(upgraded.Client(), Server, Budget)).IsEqualTo(WorkspaceDrainStop.Drained);

        await Assert.That(upgraded.Posted).IsEquivalentTo([A, B]);
        await Assert.That(Files(spool)).IsEmpty();
    }

    [Test]
    public async Task A_404_for_one_deleted_session_drops_only_its_span_when_the_probe_answers_204() {
        var spool = SpoolFor();
        WriteInOrder(spool, Span(A), Span(B), Span(B));
        var route = new ScriptedRoute(session => session == A ? HttpStatusCode.NotFound : HttpStatusCode.OK, () => HttpStatusCode.NoContent);

        await Assert.That(await spool.DrainAsync(route.Client(), Server, Budget)).IsEqualTo(WorkspaceDrainStop.Drained);

        await Assert.That(route.Posted).IsEquivalentTo([A, B, B]);
        await Assert.That(Files(spool)).IsEmpty();
        await Assert.That(spool.IsPaused).IsFalse();
    }

    public static IEnumerable<Func<HttpStatusCode?>> UnsettlingProbes() {
        yield return () => null;
        yield return () => HttpStatusCode.Unauthorized;
        yield return () => HttpStatusCode.BadGateway;
    }

    [Test]
    [MethodDataSource(nameof(UnsettlingProbes))]
    public async Task A_probe_that_settles_nothing_keeps_the_file_stops_the_pass_and_pauses_nothing(HttpStatusCode? probe) {
        var spool = SpoolFor();
        WriteInOrder(spool, Span(A), Span(B));
        var route = new ScriptedRoute(_ => HttpStatusCode.NotFound, () => probe);

        await Assert.That(await spool.DrainAsync(route.Client(), Server, Budget)).IsEqualTo(WorkspaceDrainStop.ProbeUnsettled);

        await Assert.That(route.Posted).IsEquivalentTo([A]);
        await Assert.That(Files(spool).Count).IsEqualTo(2);
        await Assert.That(spool.IsPaused).IsFalse();
    }

    [Test]
    public async Task A_temp_file_is_never_posted() {
        var spool = SpoolFor();
        spool.Write(Span(A));
        var dir   = Path.GetDirectoryName(Files(spool).Single())!;
        File.WriteAllText(Path.Combine(dir, $"{Guid.NewGuid():N}.json.{Guid.NewGuid():N}.tmp"), "{\"torn");
        var route = new ScriptedRoute(_ => HttpStatusCode.OK);

        await spool.DrainAsync(route.Client(), Server, Budget);

        await Assert.That(route.Posted).IsEquivalentTo([A]);
    }
}
